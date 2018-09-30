using System;
using System.Collections.Generic;
using System.Linq;
using D_Parser.Dom;
using D_Parser.Dom.Expressions;
using D_Parser.Parser;
using D_Parser.Resolver.ASTScanner;
using System.Collections.ObjectModel;

namespace D_Parser.Resolver.TypeResolution
{
	public static class TypeDeclarationResolver
	{
		/// <summary>
		/// Used for searching further identifier list parts.
		/// 
		/// a.b -- nextIdentifier would be 'b' whereas <param name="resultBases">resultBases</param> contained the resolution result for 'a'
		/// </summary>
		public static List<AbstractType> ResolveFurtherTypeIdentifier(int nextIdentifierHash,
			AbstractType resultBases,
			ResolutionContext ctxt,
			ISyntaxRegion typeIdObject = null, bool ufcsItem = true)
		{
			MemberSymbol statProp;
			if ((resultBases = DResolver.StripMemberSymbols(resultBases)) == null)
				return null;

			var r = new List<AbstractType>();

			foreach(var b_ in AmbiguousType.TryDissolve(resultBases))
			{
				var b = TryPostDeduceAliasDefinition(b_, typeIdObject, ctxt);

				if (b is PointerType)
					b = (b as DerivedDataType).Base;

				if (b is UserDefinedType)
				{
					var udt = b as UserDefinedType;

					using (b is MixinTemplateType || udt is TemplateType ? ctxt.Push(udt) : null)
					{
						r.AddRange(SingleNodeNameScan.SearchChildrenAndResolve(ctxt, udt, nextIdentifierHash, typeIdObject));

						statProp = StaticProperties.TryEvalPropertyType(ctxt, b, nextIdentifierHash);
						if (statProp != null)
							r.Add(statProp);

						// go the opDispatch way if possible - http://dlang.org/operatoroverloading.html#Dispatch
						if (r.Count == 0 && nextIdentifierHash != OpDispatchResolution.opDispatchId)
							r.AddRange(OpDispatchResolution.TryResolveFurtherIdViaOpDispatch(ctxt, nextIdentifierHash, udt, typeIdObject));

						if (r.Count == 0 && ufcsItem)
							r.AddRange(UFCSResolver.TryResolveUFCS(b, nextIdentifierHash, ctxt.ScopedBlock != udt.Definition && typeIdObject != null ? typeIdObject.Location : ctxt.ScopedBlock.BlockStartLocation, ctxt, typeIdObject));
					}
				}
				else if (b is PackageSymbol)
				{
					var pack = (b as PackageSymbol).Package;

					var accessedModule = pack.GetModule(nextIdentifierHash);
					if (accessedModule != null)
						r.Add(new ModuleSymbol(accessedModule, b as PackageSymbol));
					else if ((pack = pack.GetPackage(nextIdentifierHash)) != null)
						r.Add(new PackageSymbol(pack));
				}
				else if (b is ModuleSymbol)
					r.AddRange(SingleNodeNameScan.SearchChildrenAndResolve(ctxt, b as ModuleSymbol, nextIdentifierHash, typeIdObject));
				else
				{
					statProp = StaticProperties.TryEvalPropertyType(ctxt, b, nextIdentifierHash);
					if (statProp != null)
						r.Add(statProp);

					if(r.Count == 0 && ufcsItem) // Only if there hasn't been a result yet?
						r.AddRange(UFCSResolver.TryResolveUFCS (b, nextIdentifierHash, typeIdObject != null ? typeIdObject.Location : ctxt.ScopedBlock.BlockStartLocation, ctxt, typeIdObject));
				}
			}

			return r;
		}

		public static AbstractType ResolveSingle(ITypeDeclaration declaration, ResolutionContext ctxt, bool filterTemplates = true)
		{
			return declaration == null ? null : declaration.Accept (new SingleResolverVisitor (ctxt, filterTemplates));
		}

		[ThreadStatic]
		static Stack<ISyntaxRegion> aliasDeductionStack;

		internal static AbstractType TryPostDeduceAliasDefinition(AbstractType b, ISyntaxRegion typeBase, ResolutionContext ctxt)
		{
			if (typeBase != null && b is AliasedType
				&& (ctxt.Options & ResolutionOptions.DontResolveAliases) == 0)
			{
				if (aliasDeductionStack == null)
					aliasDeductionStack = new Stack<ISyntaxRegion>();
				else if (aliasDeductionStack.Contains(typeBase))
					return b;
				aliasDeductionStack.Push(typeBase);
				try
				{
					var alias = b as AliasedType;

					IEnumerable<AbstractType> aliasBase;
					if (alias.Base == null)
					{
						using (ctxt.Push(alias))
						{
							var t = DSymbolBaseTypeResolver.ResolveDVariableBaseType(alias.Definition, ctxt, true);
							aliasBase = t != null ? AmbiguousType.TryDissolve(t) : new[] { b };
						}
					}
					else
						aliasBase = AmbiguousType.TryDissolve(alias.Base);

					IEnumerable<AbstractType> bases;
					if (typeBase is TemplateInstanceExpression)
						bases = TemplateInstanceHandler.DeduceParamsAndFilterOverloads(aliasBase, typeBase as TemplateInstanceExpression, ctxt, false);
					else
						bases = TemplateInstanceHandler.DeduceParamsAndFilterOverloads(aliasBase, Enumerable.Empty<ISemantic>(), false, ctxt);

					return AmbiguousType.Get(bases);
				}
				finally
				{
					aliasDeductionStack.Pop();
				}
			}

			return b;
		}

		struct NodeMatchHandleVisitor : NodeVisitor<AbstractType>
		{
			public readonly ResolutionContext ctxt;
			public ISyntaxRegion typeBase;

			[System.Diagnostics.DebuggerStepThrough]
			public NodeMatchHandleVisitor(ResolutionContext ctxt, ISyntaxRegion typeBase)
			{
				this.ctxt = ctxt;
				this.typeBase = typeBase;
			}

			public AbstractType Visit(DEnumValue n)
			{
				return new MemberSymbol(n, null, ctxt.DeducedTypesInHierarchy);
			}

			AbstractType VisitAliasDefinition(DVariable v)
			{
				return new AliasedType(v, null, typeBase, ctxt.DeducedTypesInHierarchy);
			}

			public AbstractType VisitDVariable(DVariable variable)
			{
				if (variable.IsAlias)
					return VisitAliasDefinition(variable);

				return new MemberSymbol(variable, null, ctxt.DeducedTypesInHierarchy);
			}

			/// <summary>
			/// Add 'superior' template parameters to the current symbol because 
			/// the parameters might be re-used in the nested class.
			/// Only pays attention to those parameter symbols that are located in the current resolution scope's AST hierarchy.
			/// </summary>
			IEnumerable<TemplateParameterSymbol> GetInvisibleTypeParameters(DNode n)
			{
				ContextFrame prev = null;
				foreach (var cf in ctxt.ContextStack) {
					// Only stay in the same AST hierarchy
					if (prev != null && cf.ScopedBlock != null && cf.ScopedBlock.Parent != prev.ScopedBlock)
						yield break;
					prev = cf;

					foreach (var kv in cf.DeducedTemplateParameters)
						if (!n.ContainsTemplateParameter (kv.Value.Parameter))
							yield return kv.Value;
				}
			}

			public AbstractType Visit(EponymousTemplate ep)
			{
				return new EponymousTemplateType(ep, new ReadOnlyCollection<TemplateParameterSymbol>(new List<TemplateParameterSymbol>(GetInvisibleTypeParameters(ep))));
			}

			public AbstractType Visit(DMethod m)
			{
				return new MemberSymbol(m, null, GetInvisibleTypeParameters(m));
			}

			public AbstractType Visit(DClassLike dc)
			{
				var invisibleTypeParams = GetInvisibleTypeParameters(dc);

				switch (dc.ClassType)
				{
					case DTokens.Struct:
						return new StructType(dc, invisibleTypeParams);
					case DTokens.Union:
						return new UnionType(dc, invisibleTypeParams);
					case DTokens.Interface:
						return new InterfaceType(dc, null, invisibleTypeParams);
					case DTokens.Class:
						return new ClassType(dc, null, null, invisibleTypeParams);
					case DTokens.Template:
						if (dc.ContainsAnyAttribute(DTokens.Mixin))
							return new MixinTemplateType(dc, invisibleTypeParams);
						return new TemplateType(dc, invisibleTypeParams);

					default:
						ctxt.LogError(new NothingFoundError(dc, "Unknown type (" + DTokens.GetTokenString(dc.ClassType) + ")"));
						return null;
				}
			}

			public AbstractType Visit(DEnum de)
			{
				return new EnumType(de, de.Type == null ? new PrimitiveType(DTokens.Int) : null);
			}

			public AbstractType Visit(DModule mod)
			{
				if (typeBase != null && typeBase.ToString() != mod.ModuleName)
				{
					var pack = ctxt.ParseCache.LookupPackage(ctxt.ScopedBlock, typeBase.ToString()).FirstOrDefault();
					if (pack != null)
						return new PackageSymbol(pack);
				}

				return new ModuleSymbol(mod);
			}

			public AbstractType Visit(DBlockNode dBlockNode)
			{
				throw new NotImplementedException();
			}

			public AbstractType Visit(TemplateParameter.Node tpn)
			{
				return new TemplateParameterSymbol(tpn, null);
			}

			public AbstractType Visit(NamedTemplateMixinNode n)
			{
				return VisitDVariable(n as DVariable);
			}

			public AbstractType Visit(ImportSymbolNode importSymbolNode)
			{
				return VisitAliasDefinition(importSymbolNode);
			}

			public AbstractType Visit(ModuleAliasNode moduleAliasNode)
			{
				return VisitAliasDefinition(moduleAliasNode);
			}

			public AbstractType Visit(ImportSymbolAlias importSymbolAlias)
			{
				return VisitAliasDefinition(importSymbolAlias);
			}

			#region Attributes etc.
			public AbstractType VisitAttribute(Modifier attr)
			{
				throw new NotImplementedException();
			}

			public AbstractType VisitAttribute(DeprecatedAttribute a)
			{
				throw new NotImplementedException();
			}

			public AbstractType VisitAttribute(PragmaAttribute attr)
			{
				throw new NotImplementedException();
			}

			public AbstractType VisitAttribute(BuiltInAtAttribute a)
			{
				throw new NotImplementedException();
			}

			public AbstractType VisitAttribute(UserDeclarationAttribute a)
			{
				throw new NotImplementedException();
			}

			public AbstractType VisitAttribute(VersionCondition a)
			{
				throw new NotImplementedException();
			}

			public AbstractType VisitAttribute(DebugCondition a)
			{
				throw new NotImplementedException();
			}

			public AbstractType VisitAttribute(StaticIfCondition a)
			{
				throw new NotImplementedException();
			}

			public AbstractType VisitAttribute(NegatedDeclarationCondition a)
			{
				throw new NotImplementedException();
			}
			#endregion
		}

		/// <summary>
		/// The variable's or method's base type will be resolved (if auto type, the intializer's type will be taken).
		/// A class' base class will be searched.
		/// etc..
		/// </summary>
		public static AbstractType HandleNodeMatch(
			INode m,
			ResolutionContext ctxt,
			AbstractType resultBase = null,
			ISyntaxRegion typeBase = null)
		{
			/*
			 * Pushing a new scope is only required if current scope cannot be found in the handled node's hierarchy.
			 * Edit: No, it is required nearly every time because of nested type declarations - then, we do need the 
			 * current block scope.
			 */
			var options = ctxt.CurrentContext.ContextDependentOptions;
			var applyOptions = ctxt.ScopedBlockIsInNodeHierarchy(m);
			IDisposable disp;
			CodeLocation loc = typeBase != null ? typeBase.Location : m.Location;

			if (resultBase is DSymbol)
				disp = ctxt.Push (resultBase as DSymbol, loc);
			else
				disp = ctxt.Push (m, loc);

			using (disp)
			{
				if (applyOptions)
					ctxt.CurrentContext.ContextDependentOptions = options;

				var noBaseResolvedType = m.Accept(new NodeMatchHandleVisitor(ctxt, typeBase));
				if (noBaseResolvedType is DSymbol)
					return DSymbolBaseTypeResolver.ResolveBaseType(noBaseResolvedType as DSymbol, ctxt, typeBase, resultBase);
				return noBaseResolvedType;
			}
		}

		public static List<AbstractType> HandleNodeMatches(
			IEnumerable<INode> matches,
			ResolutionContext ctxt,
			AbstractType resultBase = null,
			ISyntaxRegion typeDeclaration = null)
		{
			var rl = new List<AbstractType>();

			// Abbreviate a foreach-loop + List alloc
			var ll = matches as IList<INode>;
			if (ll != null && ll.Count == 1)
			{
				var returnType = ll[0] != null ? HandleNodeMatch(ll[0], ctxt, resultBase, typeDeclaration) : null;
				if (returnType != null)
					rl.Add(returnType);
				return rl;
			}

			if (matches == null)
				return rl;

			foreach (var m in matches)
			{
				if (m == null)
					continue;

				var res = HandleNodeMatch(m, ctxt, resultBase, typeDeclaration);
				if (res != null)
					rl.Add(res);
			}

			return rl;
		}
	}
}
