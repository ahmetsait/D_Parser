﻿using System;
using System.Collections.Generic;
using System.Linq;

using D_Parser.Dom;
using D_Parser.Dom.Expressions;
using D_Parser.Parser;
using D_Parser.Resolver.ExpressionSemantics.Caching;
using D_Parser.Resolver.ExpressionSemantics.CTFE;
using D_Parser.Resolver.Templates;
using D_Parser.Resolver.TypeResolution;

namespace D_Parser.Resolver.ExpressionSemantics
{
	public partial class Evaluation
	{
		/// <summary>
		/// Evaluates the identifier/template instance as usual.
		/// If the id points to a variable, the initializer/dynamic value will be evaluated using its initializer.
		/// 
		/// If ImplicitlyExecute is false but value evaluation is switched on, an InternalOverloadValue-object will be returned
		/// that keeps all overloads passed via 'overloads'
		/// </summary>
		ISemantic TryDoCTFEOrGetValueRefs(AbstractType[] overloads, IExpression idOrTemplateInstance, bool ImplicitlyExecute = true, ISymbolValue[] executionArguments=null)
		{
			if (overloads == null || overloads.Length == 0){
				EvalError(idOrTemplateInstance, "No symbols found");
				return null;
			}

			var r = overloads[0];
			const string ambigousExprMsg = "Ambiguous expression";

			if(r is TemplateParameterSymbol)
			{
				var tps = (TemplateParameterSymbol)r;
				
				if(tps.Parameter is TemplateValueParameter)
					return tps.ParameterValue;
				else if(tps.Parameter is TemplateTupleParameter)
					return new TypeValue(tps.Base);
				else if(tps.Parameter is TemplateTypeParameter && tps.Base == null)
					return new TypeValue(r);
				//TODO: Are there other evaluable template parameters?
			}
			else if (r is MemberSymbol)
			{
				var mr = (MemberSymbol)r;

				// If we've got a function here, execute it
				if (mr.Definition is DMethod)
				{
					if (ImplicitlyExecute)
					{
						if (overloads.Length > 1){
							EvalError(idOrTemplateInstance, ambigousExprMsg, overloads);
							return null;
						}
						return FunctionEvaluation.Execute((DMethod)mr.Definition, executionArguments, ValueProvider);
					}
					
					return new InternalOverloadValue(overloads);
				}
				else if (mr.Definition is DVariable)
				{
					if (overloads.Length > 1)
					{
						EvalError(idOrTemplateInstance, ambigousExprMsg, overloads);
						return null;
					}
					return new VariableValue((DVariable)mr.Definition, mr.Base);
				}
			}
			else if (r is UserDefinedType)
			{
				if (overloads.Length > 1)
				{
					EvalError(idOrTemplateInstance, ambigousExprMsg, overloads);
					return null;
				}
				return new TypeValue(r);
			}

			return null;
		}

		ISemantic E(TemplateInstanceExpression tix, bool ImplicitlyExecute = true)
		{
			var o = DResolver.StripAliasSymbols(GetOverloads(tix, ctxt));

			if (eval)
				return TryDoCTFEOrGetValueRefs(o, tix, ImplicitlyExecute);
			else
			{
				ctxt.CheckForSingleResult(o, tix);
				if (o != null)
					if (o.Length == 1)
						return o[0];
					else if (o.Length > 1)
						return new InternalOverloadValue(o);
				return null;
			}
		}

		ISemantic E(IdentifierExpression id, bool ImplicitlyExecute = true)
		{
			if (id.IsIdentifier)
			{
				var o = GetOverloads(id, ctxt);

				if (eval)
				{
					if (o == null || o.Length == 0)
						return null;

					return TryDoCTFEOrGetValueRefs(o, id, ImplicitlyExecute);
				}
				else
				{
					ctxt.CheckForSingleResult(o, id);
					if (o != null)
						if (o.Length == 1)
							return o[0];
						else if (o.Length > 1)
							return new InternalOverloadValue(o);
					return null;
				}
			}
			else
				return EvaluateLiteral(id);
		}

		ISemantic EvaluateLiteral(IdentifierExpression id)
		{
			if(eval)
			{
				var v =cache.TryGetValue(id);
				if(v != null)
					return v;
			}
			byte tt = 0;

			switch (id.Format)
			{
				case Parser.LiteralFormat.CharLiteral:
					var tk = id.Subformat == LiteralSubformat.Utf32 ? DTokens.Dchar :
						id.Subformat == LiteralSubformat.Utf16 ? DTokens.Wchar :
						DTokens.Char;

					if (eval)
						return cache.Cache(id,new PrimitiveValue(tk, Convert.ToDecimal((int)(char)id.Value), id));
					else
						return new PrimitiveType(tk, 0, id);

				case LiteralFormat.FloatingPoint | LiteralFormat.Scalar:
					var im = id.Subformat.HasFlag(LiteralSubformat.Imaginary);

					tt = im ? DTokens.Idouble : DTokens.Double;

					if (id.Subformat.HasFlag(LiteralSubformat.Float))
						tt = im ? DTokens.Ifloat : DTokens.Float;
					else if (id.Subformat.HasFlag(LiteralSubformat.Real))
						tt = im ? DTokens.Ireal : DTokens.Real;

					var v = Convert.ToDecimal(id.Value);

					if (eval)
						return cache.Cache(id,new PrimitiveValue(tt, im ? 0 : v, id, im? v : 0));
					else
						return new PrimitiveType(tt, 0, id);

				case LiteralFormat.Scalar:
					var unsigned = id.Subformat.HasFlag(LiteralSubformat.Unsigned);

					if (id.Subformat.HasFlag(LiteralSubformat.Long))
						tt = unsigned ? DTokens.Ulong : DTokens.Long;
					else
						tt = unsigned ? DTokens.Uint : DTokens.Int;

					return eval ? (ISemantic)cache.Cache(id,new PrimitiveValue(tt, Convert.ToDecimal(id.Value), id)) : new PrimitiveType(tt, 0, id);

				case Parser.LiteralFormat.StringLiteral:
				case Parser.LiteralFormat.VerbatimStringLiteral:

					var _t = GetStringType(id.Subformat);
					return eval ? (ISemantic)cache.Cache(id,new ArrayValue(_t, id)) : _t;
			}
			return null;
		}




		public AbstractType[] GetOverloads(TemplateInstanceExpression tix, IEnumerable<AbstractType> resultBases = null, bool deduceParameters = true)
		{
			return GetOverloads(tix, ctxt, resultBases, deduceParameters);
		}

		public static AbstractType[] GetOverloads(TemplateInstanceExpression tix, ResolutionContext ctxt, IEnumerable<AbstractType> resultBases = null, bool deduceParameters = true)
		{
			AbstractType[] res = null;
			if (resultBases == null)
				res = TypeDeclarationResolver.ResolveIdentifier(tix.TemplateIdentifier.Id, ctxt, tix, tix.TemplateIdentifier.ModuleScoped);
			else
				res = TypeDeclarationResolver.ResolveFurtherTypeIdentifier(tix.TemplateIdentifier.Id, resultBases, ctxt, tix);

			return !ctxt.Options.HasFlag(ResolutionOptions.NoTemplateParameterDeduction) && deduceParameters ?
				TemplateInstanceHandler.DeduceParamsAndFilterOverloads(res, tix, ctxt) : res;
		}

		public AbstractType[] GetOverloads(IdentifierExpression id, bool deduceParameters = true)
		{
			return GetOverloads(id, ctxt, deduceParameters);
		}

		public static AbstractType[] GetOverloads(IdentifierExpression id, ResolutionContext ctxt, bool deduceParameters = true)
		{
			var raw=TypeDeclarationResolver.ResolveIdentifier(id.Value as string, ctxt, id, id.ModuleScoped);
			var f = DResolver.FilterOutByResultPriority(ctxt, raw);
			
			if(f==null)
				return null;
			
			return !ctxt.Options.HasFlag(ResolutionOptions.NoTemplateParameterDeduction) && deduceParameters ?
				TemplateInstanceHandler.DeduceParamsAndFilterOverloads(f, null, false, ctxt) : 
				f.ToArray();
		}
	}
}
