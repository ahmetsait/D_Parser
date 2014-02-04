﻿using D_Parser.Dom;
using D_Parser.Dom.Expressions;
using D_Parser.Parser;
using D_Parser.Resolver.TypeResolution;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace D_Parser.Resolver.ExpressionSemantics
{
	public class AmbiguousType : AbstractType
	{
		public AbstractType[] Overloads;

		public AmbiguousType(AbstractType[] o)
		{
			Overloads = o;
		}

		public AmbiguousType(IEnumerable<AbstractType> o)
		{
			Overloads = o.ToArray();
		}

		public override string ToCode()
		{
			return "<Overloads>";
		}
	}

	public class ExpressionTypeEvaluation : ExpressionVisitor<AbstractType>
	{
		#region Properties / LowLevel
		private readonly ResolutionContext ctxt;
		public readonly List<EvaluationException> Errors = new List<EvaluationException>();

		ArrayType GetStringType(LiteralSubformat fmt = LiteralSubformat.Utf8)
		{
			return Evaluation.GetStringType(ctxt, fmt);
		}

		#region Errors
		bool ignoreErrors = false;
		internal void EvalError(EvaluationException ex)
		{
			if (!ignoreErrors)
				Errors.Add(ex);
		}

		internal void EvalError(IExpression x, string msg, ISemantic[] lastResults = null)
		{
			if (!ignoreErrors)
				Errors.Add(new EvaluationException(x, msg, lastResults));
		}

		internal void EvalError(IExpression x, string msg, ISemantic lastResult)
		{
			if (!ignoreErrors)
				Errors.Add(new EvaluationException(x, msg, new[] { lastResult }));
		}
		#endregion
		#endregion

		#region Ctor/IO
		public ExpressionTypeEvaluation(ResolutionContext ctxt)
		{
			this.ctxt = ctxt;
		}

		/// <summary>
		/// Since most expressions should return a single type only, it's not needed to use this function unless you might
		/// want to pay attention on (illegal) multiple overloads.
		/// </summary>
		public static AbstractType[] EvaluateTypes(IExpression x, ResolutionContext ctxt)
		{
			var ev = new ExpressionTypeEvaluation(ctxt);
			AbstractType t;
			if (!Debugger.IsAttached)
				try { t = x.Accept(ev); }
				catch { t = null; }
			else
				t = x.Accept(ev);

			if (t is AmbiguousType)
				return ((AmbiguousType)t).Overloads;

			return t == null ? null : new[] { t };
		}

		public static AbstractType EvaluateType(IExpression x, ResolutionContext ctxt)
		{
			var ev = new ExpressionTypeEvaluation(ctxt);
			
			if (!Debugger.IsAttached)
				try { return x.Accept(ev); }
				catch { return null; }
			else
				return x.Accept(ev);
		}
		#endregion

		#region Method (overloads)
		public AbstractType Visit(PostfixExpression_MethodCall call)
		{
			List<ISemantic> callArgs;
			ISymbolValue delegValue;

			AbstractType[] baseExpression;
			TemplateInstanceExpression tix;

			GetRawCallOverloads(ctxt, call, out baseExpression, out tix);

			var argTypeFilteredOverloads = Evaluation.EvalMethodCall(baseExpression, null, tix, ctxt, call, out callArgs, out delegValue, !ctxt.Options.HasFlag(ResolutionOptions.ReturnMethodReferencesOnly));

			// Check if one overload remains and return that one.
			ctxt.CheckForSingleResult(argTypeFilteredOverloads, call);
			return argTypeFilteredOverloads != null && argTypeFilteredOverloads.Count != 0 ? argTypeFilteredOverloads[0] : null;
		}

		void GetRawCallOverloads(ResolutionContext ctxt, PostfixExpression_MethodCall call,
			out AbstractType[] baseExpression,
			out TemplateInstanceExpression tix)
		{
			tix = null;

			if (call.PostfixForeExpression is PostfixExpression_Access)
			{
				var pac = (PostfixExpression_Access)call.PostfixForeExpression;
				tix = pac.AccessExpression as TemplateInstanceExpression;

				baseExpression = Evaluation.EvalPostfixAccessExpression(this, ctxt, pac, null, false, false);
			}
			else
			{
				// Explicitly don't resolve the methods' return types - it'll be done after filtering to e.g. resolve template types to the deduced one
				var optBackup = ctxt.CurrentContext.ContextDependentOptions;
				ctxt.CurrentContext.ContextDependentOptions |= ResolutionOptions.DontResolveBaseTypes;

				if (call.PostfixForeExpression is TokenExpression)
					baseExpression = ExpressionTypeEvaluation.GetResolvedConstructorOverloads((TokenExpression)call.PostfixForeExpression, ctxt);
				else 
				{
					if (call.PostfixForeExpression is TemplateInstanceExpression)
						baseExpression = ExpressionTypeEvaluation.GetOverloads(tix = (TemplateInstanceExpression)call.PostfixForeExpression, ctxt, null, false);
					else if (call.PostfixForeExpression is IdentifierExpression)
						baseExpression = ExpressionTypeEvaluation.GetOverloads(call.PostfixForeExpression as IdentifierExpression, ctxt, deduceParameters: false);
					else
						baseExpression = new[] { call.PostfixForeExpression != null ? AbstractType.Get(call.PostfixForeExpression.Accept(this)) : null };
				}

				ctxt.CurrentContext.ContextDependentOptions = optBackup;
			}
		}

		public static AbstractType[] GetUnfilteredMethodOverloads(IExpression foreExpression, ResolutionContext ctxt, IExpression supExpression = null)
		{
			AbstractType[] overloads = null;

			if (foreExpression is TemplateInstanceExpression)
				overloads = GetOverloads(foreExpression as TemplateInstanceExpression, ctxt, null);
			else if (foreExpression is IdentifierExpression)
				overloads = GetOverloads(foreExpression as IdentifierExpression, ctxt, deduceParameters: false);
			else if (foreExpression is PostfixExpression_Access)
				overloads = GetAccessedOverloads(foreExpression as PostfixExpression_Access, ctxt, null, false);
			else if (foreExpression is TokenExpression)
				overloads = GetResolvedConstructorOverloads((TokenExpression)foreExpression, ctxt);
			else
				overloads = new[] { EvaluateType(foreExpression, ctxt) };

			var l = new List<AbstractType>();
			bool staticOnly = true;

			foreach (var ov in DResolver.StripAliasSymbols(overloads))
			{
				var t = ov;
				if (ov is MemberSymbol)
				{
					var ms = ov as MemberSymbol;
					if (ms.Definition is Dom.DMethod)
					{
						l.Add(ms);
						continue;
					}

					staticOnly = false;
					t = DResolver.StripAliasSymbol(ms.Base);
				}

				if (t is TemplateIntermediateType)
				{
					var tit = t as TemplateIntermediateType;

					var m = TypeDeclarationResolver.HandleNodeMatches(
						GetOpCalls(tit, staticOnly), ctxt,
						null, supExpression ?? foreExpression);

					/*
					 * On structs, there must be a default () constructor all the time.
					 * If there are (other) constructors in structs, the explicit member initializer constructor is not
					 * provided anymore. This will be handled in the GetConstructors() method.
					 * If there are opCall overloads, canCreateeExplicitStructCtor overrides the ctor existence check in GetConstructors()
					 * and enforces that the explicit ctor will not be generated.
					 * An opCall overload with no parameters supersedes the default ctor.
					 */
					var canCreateExplicitStructCtor = m == null || m.Length == 0;

					if (!canCreateExplicitStructCtor)
						l.AddRange(m);

					m = TypeDeclarationResolver.HandleNodeMatches(
						GetConstructors(tit, canCreateExplicitStructCtor), ctxt,
						null, supExpression ?? foreExpression);

					if (m != null && m.Length != 0)
						l.AddRange(m);
				}
				else
					l.Add(ov);
			}

			return l.ToArray();
		}

		public static AbstractType[] GetAccessedOverloads(PostfixExpression_Access acc, ResolutionContext ctxt,
			ISemantic resultBase = null, bool DeducePostfixTemplateParams = true)
		{
			return Evaluation.EvalPostfixAccessExpression<AbstractType>(new ExpressionTypeEvaluation(ctxt), ctxt, acc, resultBase, DeducePostfixTemplateParams);
		}

		public static AbstractType[] GetResolvedConstructorOverloads(TokenExpression tk, ResolutionContext ctxt)
		{
			if (tk.Token == DTokens.This || tk.Token == DTokens.Super)
			{
				var classRef = EvaluateType(tk, ctxt) as TemplateIntermediateType;

				if (classRef != null)
					return D_Parser.Resolver.TypeResolution.TypeDeclarationResolver.HandleNodeMatches(GetConstructors(classRef), ctxt, classRef, tk);
			}
			return null;
		}

		/// <summary>
		/// Returns all constructors from the given class or struct.
		/// If no explicit constructor given, an artificial implicit constructor method stub will be created.
		/// </summary>
		public static IEnumerable<DMethod> GetConstructors(TemplateIntermediateType ct, bool canCreateExplicitStructCtor = true)
		{
			bool foundExplicitCtor = false;

			// Simply get all constructors that have the ctor id assigned. Makin' it faster ;)
			var ch = ct.Definition[DMethod.ConstructorIdentifier];
			if (ch != null)
				foreach (var m in ch)
				{
					// Not to forget: 'this' aliases are also possible - so keep checking for m being a genuine ctor
					var dm = m as DMethod;
					if (dm != null && dm.SpecialType == DMethod.MethodType.Constructor)
					{
						yield return dm;
						foundExplicitCtor = true;
					}
				}

			var isStruct = ct is StructType;
			if (!foundExplicitCtor || isStruct)
			{
				// Check if there is an opCall that has no parameters.
				// Only if no exists, it's allowed to make a default parameter.
				bool canMakeDefaultCtor = true;
				foreach (var opCall in GetOpCalls(ct, true))
					if (opCall.Parameters == null || opCall.Parameters.Count == 0)
					{
						canMakeDefaultCtor = false;
						break;
					}

				if (canMakeDefaultCtor)
					yield return new DMethod(DMethod.MethodType.Constructor) { Name = DMethod.ConstructorIdentifier, Parent = ct.Definition, Description = "Default constructor for " + ct.Name };

				// If struct, there's also a ctor that has all struct members as parameters.
				// Only, if there are no explicit ctors nor opCalls
				if (isStruct && !foundExplicitCtor && canCreateExplicitStructCtor)
				{
					var l = new List<INode>();

					foreach (var member in ct.Definition)
					{
						var dv = member as DVariable;
						if (dv != null &&
							!dv.IsStatic &&
							!dv.IsAlias &&
							!dv.IsConst) //TODO dunno if public-ness of items is required..
							l.Add(dv);
					}

					yield return new DMethod(DMethod.MethodType.Constructor)
					{
						Name = DMethod.ConstructorIdentifier,
						Parent = ct.Definition,
						Description = "Default constructor for struct " + ct.Name,
						Parameters = l
					};
				}
			}
		}

		public static IEnumerable<DMethod> GetOpCalls(TemplateIntermediateType t, bool staticOnly)
		{
			var opCall = t.Definition["opCall"];
			if (opCall != null)
				foreach (var call in opCall)
				{
					var dm = call as DMethod;
					if (dm != null && (!staticOnly || dm.IsStatic))
						yield return dm;
				}
		}

		#endregion

		#region Infix (op-based) expressions
		AbstractType OpExpressionType(OperatorBasedExpression x)
		{
			var t = x.LeftOperand != null ? x.LeftOperand.Accept(this) : null;

			if (t != null)
				return t;

			return x.RightOperand != null ? x.RightOperand.Accept(this) : null;
		}

		public AbstractType Visit(AssignExpression x)
		{
			return OpExpressionType(x);
		}

		public AbstractType Visit(ConditionalExpression x)
		{
			return x.TrueCaseExpression != null ? x.TrueCaseExpression.Accept(this) : (x.FalseCaseExpression != null ? x.FalseCaseExpression.Accept(this) : null);
		}

		public AbstractType Visit(OrOrExpression x)
		{
			return new PrimitiveType(DTokens.Bool);
		}

		public AbstractType Visit(AndAndExpression x)
		{
			return new PrimitiveType(DTokens.Bool);
		}

		public AbstractType Visit(XorExpression x)
		{
			return OpExpressionType(x);
		}

		public AbstractType Visit(OrExpression x)
		{
			return OpExpressionType(x);
		}

		public AbstractType Visit(AndExpression x)
		{
			return OpExpressionType(x);
		}

		public AbstractType Visit(EqualExpression x)
		{
			return new PrimitiveType(DTokens.Bool);
		}

		public AbstractType Visit(IdentityExpression x)
		{
			return new PrimitiveType(DTokens.Bool);
		}

		public AbstractType Visit(RelExpression x)
		{
			return new PrimitiveType(DTokens.Bool);
		}

		public AbstractType Visit(InExpression x)
		{
			return x.RightOperand != null ? x.RightOperand.Accept(this) : null;
		}

		public AbstractType Visit(ShiftExpression x)
		{
			return OpExpressionType(x);
		}

		public AbstractType Visit(AddExpression x)
		{
			return OpExpressionType(x);
		}

		public AbstractType Visit(MulExpression x)
		{
			return OpExpressionType(x);
		}

		public AbstractType Visit(CatExpression x)
		{
			return OpExpressionType(x);
		}

		public AbstractType Visit(PowExpression x)
		{
			return OpExpressionType(x);
		}
		#endregion

		#region Prefix (unary) experssions
		public AbstractType Visit(CastExpression ce)
		{
			AbstractType castedType = null;

			if (ce.Type != null)
			{
				var castedTypes = TypeDeclarationResolver.Resolve(ce.Type, ctxt);

				ctxt.CheckForSingleResult(castedTypes, ce.Type);

				if (castedTypes != null && castedTypes.Length != 0)
					castedType = castedTypes[0];
			}
			else if (ce.UnaryExpression != null)
			{
				castedType = AbstractType.Get(ce.UnaryExpression.Accept(this));

				if (castedType != null && ce.CastParamTokens != null && ce.CastParamTokens.Length > 0)
				{
					//TODO: Wrap resolved type with member function attributes
				}
			}

			return castedType;
		}

		public AbstractType Visit(UnaryExpression_Cat x) // ~b;
		{
			return x.UnaryExpression.Accept(this);
		}

		public AbstractType Visit(UnaryExpression_Increment x)
		{
			return x.UnaryExpression.Accept(this);
		}

		public AbstractType Visit(UnaryExpression_Decrement x)
		{
			return x.UnaryExpression.Accept(this);
		}

		public AbstractType Visit(UnaryExpression_Add x)
		{
			return x.UnaryExpression.Accept(this);
		}

		public AbstractType Visit(UnaryExpression_Sub x)
		{
			return x.UnaryExpression.Accept(this);
		}

		public AbstractType Visit(UnaryExpression_Not x)
		{
			return x.UnaryExpression.Accept(this);
		}

		public AbstractType Visit(UnaryExpression_Mul x)
		{
			return x.UnaryExpression.Accept(this);
		}

		public AbstractType Visit(UnaryExpression_And x)
		{
			return new PointerType(x.UnaryExpression.Accept(this), x);
		}

		public AbstractType Visit(DeleteExpression x)
		{
			return null;
		}

		public AbstractType Visit(UnaryExpression_Type x)
		{
			var uat = x as UnaryExpression_Type;

			if (uat.Type == null)
				return null;

			var types = TypeDeclarationResolver.Resolve(uat.Type, ctxt);
			ctxt.CheckForSingleResult(types, uat.Type);

			if (types != null && types.Length != 0)
			{
				var res = TypeDeclarationResolver.Resolve(new IdentifierDeclaration(uat.AccessIdentifierHash) { EndLocation = uat.EndLocation }, ctxt, types);

				ctxt.CheckForSingleResult(res, x);

				if (res != null && res.Length != 0)
					return res[0];
			}

			return null;
		}

		public AbstractType Visit(NewExpression nex)
		{
			// http://www.d-programming-language.org/expression.html#NewExpression
			AbstractType[] possibleTypes;

			if (nex.Type is IdentifierDeclaration)
				possibleTypes = TypeDeclarationResolver.Resolve((IdentifierDeclaration)nex.Type, ctxt, filterForTemplateArgs: false);
			else
				possibleTypes = TypeDeclarationResolver.Resolve(nex.Type, ctxt);

			var ctors = new Dictionary<DMethod, TemplateIntermediateType>();

			if (possibleTypes == null)
				return null;

			foreach (var t in possibleTypes)
			{
				var ct = DResolver.StripAliasSymbol(t as AbstractType) as TemplateIntermediateType;
				if (ct != null &&
					!ct.Definition.ContainsAttribute(DTokens.Abstract))
					foreach (var ctor in GetConstructors(ct))
						ctors.Add(ctor, ct);
			}

			MemberSymbol finalCtor = null;

			var kvArray = ctors.ToArray();

			/*
			 * TODO: Determine argument types and filter out ctor overloads.
			 */

			if (kvArray.Length != 0)
				finalCtor = new MemberSymbol(kvArray[0].Key, kvArray[0].Value, nex);
			else if (possibleTypes.Length != 0)
				return AbstractType.Get(possibleTypes[0]);

			return finalCtor;
		}
		#endregion

		#region Postfix expressions
		AbstractType EvalForeExpression(PostfixExpression ex)
		{
			var foreExpr = ex.PostfixForeExpression != null ? ex.PostfixForeExpression.Accept(this) : null;

			if (foreExpr is AliasedType)
				foreExpr = DResolver.StripAliasSymbol(foreExpr);
			
			if (foreExpr == null)
				ctxt.LogError(new NothingFoundError(ex.PostfixForeExpression));

			return foreExpr;
		}

		public AbstractType Visit(PostfixExpression_Access ex)
		{
			var r = Evaluation.EvalPostfixAccessExpression(this, ctxt, ex);
			ctxt.CheckForSingleResult(r, ex);

			return r != null && r.Length != 0 ? r[0] : null;
		}

		public AbstractType Visit(PostfixExpression_Increment x)
		{
			return EvalForeExpression(x);
		}

		public AbstractType Visit(PostfixExpression_Decrement x)
		{
			return EvalForeExpression(x);
		}

		public AbstractType Visit(PostfixExpression_Index x)
		{
			var foreExpression = EvalForeExpression(x);

			// myArray[0]; myArray[0..5];
			// opIndex/opSlice ?
			if (foreExpression is MemberSymbol)
				foreExpression = DResolver.StripMemberSymbols(foreExpression);

			foreExpression = DResolver.StripMemberSymbols(foreExpression);

			if (foreExpression is AssocArrayType)
			{
				var ar = foreExpression as AssocArrayType;
				/*
				 * myType_Array[0] -- returns TypeResult myType
				 * return the value type of a given array result
				 */
				//TODO: Handle opIndex overloads

				return new ArrayAccessSymbol(x, ar.ValueType);
			}
			/*
			 * int* a = new int[10];
			 * 
			 * a[0] = 12;
			 */
			else if (foreExpression is PointerType)
				return (foreExpression as PointerType).Base;
			//return new ArrayAccessSymbol(x,((PointerType)foreExpression).Base);

			else if (foreExpression is DTuple)
			{
				var tt = foreExpression as DTuple;

				if (x.Arguments != null && x.Arguments.Length != 0)
				{
					var idx = Evaluation.EvaluateValue(x.Arguments[0], ctxt) as PrimitiveValue;

					if (idx == null || !DTokens.IsBasicType_Integral(idx.BaseTypeToken))
					{
						ctxt.LogError(x.Arguments[0], "Index expression must evaluate to integer value");
					}
					else if (idx.Value > (decimal)Int32.MaxValue ||
							 (int)idx.Value >= tt.Items.Length ||
							 (int)idx.Value < 0)
					{
						ctxt.LogError(x.Arguments[0], "Index number must be a value between 0 and " + tt.Items.Length);
					}
					else
					{
						return AbstractType.Get(tt.Items[(int)idx.Value]);
					}
				}
			}

			ctxt.LogError(new ResolutionError(x, "Invalid base type for index expression"));
			return null;
		}

		public AbstractType Visit(PostfixExpression_Slice x)
		{
			var foreExpression = EvalForeExpression(x);

			// myArray[0]; myArray[0..5];
			// opIndex/opSlice ?
			if (foreExpression is MemberSymbol)
				foreExpression = DResolver.StripMemberSymbols(foreExpression);

			return foreExpression; // Still of the array's type.
		}
		#endregion

		#region Identifier primitives
		public AbstractType Visit(TemplateInstanceExpression tix)
		{
			var o = DResolver.StripAliasSymbols(GetOverloads(tix, ctxt));
			
			ctxt.CheckForSingleResult(o, tix);
			if (o != null)
				if (o.Length == 1)
					return o[0];
				else if (o.Length > 1)
					return new AmbiguousType(o);
			return null;
		}

		public AbstractType Visit(IdentifierExpression id)
		{
			if (id.IsIdentifier)
			{
				var o = GetOverloads(id, ctxt);

				ctxt.CheckForSingleResult(o, id);
				if (o != null)
					if (o.Length == 1)
						return o[0];
					else if (o.Length > 1)
						return new AmbiguousType(o);
				return null;
			}

			byte tt;
			switch (id.Format)
			{
				case Parser.LiteralFormat.CharLiteral:
					var tk = id.Subformat == LiteralSubformat.Utf32 ? DTokens.Dchar :
						id.Subformat == LiteralSubformat.Utf16 ? DTokens.Wchar :
						DTokens.Char;

					return new PrimitiveType(tk, 0, id);

				case LiteralFormat.FloatingPoint | LiteralFormat.Scalar:
					var im = id.Subformat.HasFlag(LiteralSubformat.Imaginary);

					tt = im ? DTokens.Idouble : DTokens.Double;

					if (id.Subformat.HasFlag(LiteralSubformat.Float))
						tt = im ? DTokens.Ifloat : DTokens.Float;
					else if (id.Subformat.HasFlag(LiteralSubformat.Real))
						tt = im ? DTokens.Ireal : DTokens.Real;

					return new PrimitiveType(tt, 0, id);

				case LiteralFormat.Scalar:
					var unsigned = id.Subformat.HasFlag(LiteralSubformat.Unsigned);

					if (id.Subformat.HasFlag(LiteralSubformat.Long))
						tt = unsigned ? DTokens.Ulong : DTokens.Long;
					else
						tt = unsigned ? DTokens.Uint : DTokens.Int;

					return new PrimitiveType(tt, 0, id);

				case Parser.LiteralFormat.StringLiteral:
				case Parser.LiteralFormat.VerbatimStringLiteral:
					return GetStringType(id.Subformat);
				default:
					return null;
			}
		}

		public AbstractType[] GetOverloads(TemplateInstanceExpression tix, IEnumerable<AbstractType> resultBases = null, bool deduceParameters = true)
		{
			return GetOverloads(tix, ctxt, resultBases, deduceParameters);
		}

		public static AbstractType[] GetOverloads(TemplateInstanceExpression tix, ResolutionContext ctxt, IEnumerable<AbstractType> resultBases = null, bool deduceParameters = true)
		{
			if (resultBases == null && tix.InnerDeclaration != null)
				resultBases = TypeDeclarationResolver.Resolve(tix.InnerDeclaration, ctxt);

			AbstractType[] res;
			if (resultBases == null)
				res = TypeDeclarationResolver.ResolveIdentifier(tix.TemplateIdHash, ctxt, tix, tix.ModuleScopedIdentifier);
			else
				res = TypeDeclarationResolver.ResolveFurtherTypeIdentifier(tix.TemplateIdHash, resultBases, ctxt, tix);

			return (ctxt.Options & ResolutionOptions.NoTemplateParameterDeduction) == 0 && deduceParameters ?
				TemplateInstanceHandler.DeduceParamsAndFilterOverloads(res, tix, ctxt) : res;
		}

		public AbstractType[] GetOverloads(IdentifierExpression id, IEnumerable<AbstractType> resultBases = null, bool deduceParameters = true)
		{
			return GetOverloads(id, ctxt, resultBases, deduceParameters);
		}

		public static AbstractType[] GetOverloads(IdentifierExpression id, ResolutionContext ctxt, IEnumerable<AbstractType> resultBases = null, bool deduceParameters = true)
		{
			AbstractType[] res;
			if (resultBases == null)
				res = TypeDeclarationResolver.ResolveIdentifier(id.ValueStringHash, ctxt, id, id.ModuleScoped);
			else
				res = TypeDeclarationResolver.ResolveFurtherTypeIdentifier(id.ValueStringHash, resultBases, ctxt, id);

			if (res == null)
				return null;

			var f = DResolver.FilterOutByResultPriority(ctxt, res);

			if (f.Count == 0)
				return null;

			return (ctxt.Options & ResolutionOptions.NoTemplateParameterDeduction) == 0 && deduceParameters ?
				TemplateInstanceHandler.DeduceParamsAndFilterOverloads(f, null, false, ctxt) :
				f.ToArray();
		}
#endregion

		#region Primitive expressions
		public AbstractType Visit(Expression ex)
		{
			return ex.Expressions.Count == 0 ? null : ex.Expressions[ex.Expressions.Count - 1].Accept(this);
		}

		public AbstractType Visit(AnonymousClassExpression x)
		{
			throw new NotImplementedException();
		}

		public AbstractType Visit(TokenExpression x)
		{
			switch (x.Token)
			{
				// References current class scope
				case DTokens.This:
					var classDef = ctxt.ScopedBlock;

					while (!(classDef is DClassLike) && classDef != null)
						classDef = classDef.Parent as IBlockNode;

					if (classDef is DClassLike)
						return TypeDeclarationResolver.HandleNodeMatch(classDef, ctxt, null, x);

					//TODO: Throw
					return null;


				case DTokens.Super:
					// References super type of currently scoped class declaration

					classDef = ctxt.ScopedBlock;

					while (!(classDef is DClassLike) && classDef != null)
						classDef = classDef.Parent as IBlockNode;

					if (classDef != null)
					{
						var tr = DResolver.ResolveBaseClasses(new ClassType(classDef as DClassLike, null, null), ctxt, true);

						if (tr.Base != null)
						{
							// Important: Overwrite type decl base with 'super' token
							tr.Base.DeclarationOrExpressionBase = x;

							return tr.Base;
						}
					}

					//TODO: Throw
					return null;

				case DTokens.Null:
					return null;

				case DTokens.Dollar:
					return new PrimitiveType(DTokens.Int); // Really integer or every kind of iterator type?

				case DTokens.False:
				case DTokens.True:
					return new PrimitiveType(DTokens.Bool);

				case DTokens.__FILE__:
					return GetStringType();
				case DTokens.__LINE__:
					return new PrimitiveType(DTokens.Int);
				case DTokens.__MODULE__:
					return GetStringType();
				case DTokens.__FUNCTION__:
				//TODO
					return null;
				case DTokens.__PRETTY_FUNCTION__:
					return GetStringType();
				default:
					return null;
			}
		}

		public AbstractType Visit(TypeDeclarationExpression x)
		{
			// should be containing a typeof() only; static properties etc. are parsed as access expressions
			return TypeDeclarationResolver.ResolveSingle(x.Declaration, ctxt);
		}

		public AbstractType Visit(ArrayLiteralExpression arr)
		{
			if (arr.Elements != null && arr.Elements.Count > 0)
			{
				// Simply resolve the first element's type and take it as the array's value type
				var valueType = arr.Elements[0] != null ? AbstractType.Get(arr.Elements[0].Accept(this)) : null;

				return new ArrayType(valueType, arr);
			}

			ctxt.LogError(arr, "Array literal must contain at least one element.");
			return null;
		}

		public AbstractType Visit(AssocArrayExpression aa)
		{
			if (aa.Elements != null && aa.Elements.Count > 0)
			{
				var firstElement = aa.Elements[0].Key;
				var firstElementValue = aa.Elements[0].Value;

				var keyType = firstElement != null ? AbstractType.Get(firstElement.Accept(this)) : null;
				var valueType = firstElementValue != null ? AbstractType.Get(firstElementValue.Accept(this)) : null;

				return new AssocArrayType(valueType, keyType, aa);
			}

			return null;
		}

		public AbstractType Visit(FunctionLiteral x)
		{
			return new DelegateType(
				(ctxt.Options & ResolutionOptions.DontResolveBaseTypes | ResolutionOptions.ReturnMethodReferencesOnly) != 0 ? null : TypeDeclarationResolver.GetMethodReturnType(x.AnonymousMethod, ctxt),
				x,
				TypeResolution.TypeDeclarationResolver.HandleNodeMatches(x.AnonymousMethod.Parameters, ctxt));
		}

		public AbstractType Visit(AssertExpression x)
		{
			return new PrimitiveType(DTokens.Void, 0, x);
		}

		public AbstractType Visit(MixinExpression x)
		{
			var s = Evaluation.EvaluateMixinExpressionContent(ctxt, x);

			if (s == null)
			{
				EvalError(new InvalidStringException(x));
				return null;
			}

			// Parse it as an expression
			var ex = DParser.ParseAssignExpression(s);

			if (ex == null)
			{
				EvalError(new EvaluationException(x, "Invalid expression code given"));
				return null;
			}
			//TODO: Excessive caching
			// Evaluate the expression's type/value
			return ex.Accept(this);
		}

		public AbstractType Visit(ImportExpression x)
		{
			return Evaluation.GetStringType(ctxt);
		}

		public AbstractType Visit(TypeidExpression x)
		{
			//TODO: Split up into more detailed typeinfo objects (e.g. for arrays, pointers, classes etc.)

			return TypeDeclarationResolver.ResolveSingle(new IdentifierDeclaration("TypeInfo") { InnerDeclaration = new IdentifierDeclaration("object") }, ctxt);
		}

		public AbstractType Visit(IsExpression x)
		{
			return new PrimitiveType(DTokens.Bool);
		}

		public AbstractType Visit(SurroundingParenthesesExpression x)
		{
			return x.Expression.Accept(this);
		}

		public AbstractType Visit(VoidInitializer x)
		{
			return new PrimitiveType(DTokens.Void);
		}

		public AbstractType Visit(ArrayInitializer x)
		{
			return Visit((AssocArrayExpression)x);
		}

		public AbstractType Visit(StructInitializer x)
		{
			// TODO: Create struct node with initialized members etc.
			return null;
		}

		public AbstractType Visit(StructMemberInitializer structMemberInitializer)
		{
			//TODO
			return null;
		}
		#endregion

		#region Traits
		public AbstractType Visit(TraitsExpression te)
		{
			PostfixExpression_Access pfa;
			AbstractType t;
			ResolutionOptions optionsBackup;

			switch (te.Keyword)
			{
				case "":
				case null:
					return null;

				case "identifier":
					return GetStringType();

				case "getMember":
					pfa = prepareMemberTraitExpression(te, out t);

					if (pfa == null || t == null)
						break;

					var vs = Evaluation.EvalPostfixAccessExpression(this, ctxt, pfa, t);
					if (vs == null || vs.Length == 0)
						return null;
					return vs[0];


				case "getOverloads":
					optionsBackup = ctxt.ContextIndependentOptions;
					ctxt.ContextIndependentOptions = ResolutionOptions.IgnoreAllProtectionAttributes;

					pfa = prepareMemberTraitExpression(te, out t);

					if (pfa != null && t != null)
						vs = Evaluation.EvalPostfixAccessExpression(this, ctxt, pfa, t);
					else
						vs = null;

					ctxt.ContextIndependentOptions = optionsBackup;

					return new DTuple(te, vs);


				case "getProtection":
					return GetStringType();

				case "getVirtualFunctions":
					break;
				case "getVirtualMethods":
					break;
				case "parent":
					break;
				case "classInstanceSize":
					break;
				case "allMembers":
					break;
				case "derivedMembers":
					break;

				case "compiles":
						return new PrimitiveType(DTokens.Bool);
			}

			if (te.Keyword.StartsWith("is") || te.Keyword.StartsWith("has"))
				return new PrimitiveType(DTokens.Bool);

			return null;
		}

		PostfixExpression_Access prepareMemberTraitExpression(TraitsExpression te, out AbstractType t)
		{
			return prepareMemberTraitExpression(ctxt, te, out t);
		}

		/// <summary>
		/// Used when evaluating traits.
		/// Evaluates the first argument to <param name="t">t</param>, 
		/// takes the second traits argument, tries to evaluate it to a string, and puts it + the first arg into an postfix_access expression
		/// </summary>
		internal static PostfixExpression_Access prepareMemberTraitExpression(ResolutionContext ctxt, TraitsExpression te, out AbstractType t, AbstractSymbolValueProvider vp = null)
		{
			if (te.Arguments != null && te.Arguments.Length == 2)
			{
				var tEx = te.Arguments[0];
				t = DResolver.StripMemberSymbols(ResolveTraitArgument(ctxt, tEx));

				if (t == null)
					ctxt.LogError(te, "First argument didn't resolve to a type");
				else if (te.Arguments[1].AssignExpression != null)
				{
					var litEx = te.Arguments[1].AssignExpression;
					var v = vp != null ? Evaluation.EvaluateValue(litEx, vp) : Evaluation.EvaluateValue(litEx, ctxt);
					
					if (v is ArrayValue && (v as ArrayValue).IsString)
					{
						var av = v as ArrayValue;

						// Mock up a postfix_access expression to ensure static properties & ufcs methods are checked either
						return new PostfixExpression_Access
						{
							PostfixForeExpression = tEx.AssignExpression ?? new TypeDeclarationExpression(tEx.Type),
							AccessExpression = new IdentifierExpression(av.StringValue)
							{
								Location = litEx.Location,
								EndLocation = litEx.EndLocation
							},
							EndLocation = litEx.EndLocation
						};
					}
					else
						ctxt.LogError(litEx, "Second traits argument must evaluate to a string literal");
				}
				else
					ctxt.LogError(te, "Second traits argument must be an expression");
			}

			t = null;
			return null;
		}

		public static AbstractType ResolveTraitArgument(ResolutionContext ctxt, TraitsArgument arg)
		{
			if (arg.Type != null)
				return TypeDeclarationResolver.ResolveSingle(arg.Type, ctxt);
			else if (arg.AssignExpression != null)
				return DResolver.StripAliasSymbol(EvaluateType(arg.AssignExpression, ctxt));
			else
				return null;
		}
		#endregion
	}
}
