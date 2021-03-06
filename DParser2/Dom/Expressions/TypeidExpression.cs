﻿using System;
using System.Collections.Generic;
using D_Parser.Parser;

namespace D_Parser.Dom.Expressions
{
	public class TypeidExpression : PrimaryExpression,ContainerExpression
	{
		public ITypeDeclaration Type;
		public IExpression Expression;

		public override string ToString()
		{
			return "typeid(" + (Type != null ? Type.ToString() : Expression.ToString()) + ")";
		}

		public CodeLocation Location
		{
			get;
			set;
		}

		public CodeLocation EndLocation
		{
			get;
			set;
		}

		public IEnumerable<IExpression> SubExpressions
		{
			get
			{ 
				if (Expression != null)
					yield return Expression;
				else if (Type != null)
					yield return TypeDeclarationExpression.TryWrap(Type);
			}
		}

		public void Accept(ExpressionVisitor vis)
		{
			vis.Visit(this);
		}

		public R Accept<R>(ExpressionVisitor<R> vis)
		{
			return vis.Visit(this);
		}
	}
}

