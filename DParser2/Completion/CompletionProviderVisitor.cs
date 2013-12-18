﻿//
// CompletionProviderVisitor.cs
//
// Author:
//       Alexander Bothe <info@alexanderbothe.com>
//
// Copyright (c) 2013 Alexander Bothe
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using D_Parser.Dom;
using D_Parser.Dom.Expressions;
using D_Parser.Dom.Statements;
using D_Parser.Parser;
using System.Collections.Generic;
using D_Parser.Completion.Providers;

namespace D_Parser.Completion
{
	public class CompletionProviderVisitor : DefaultDepthFirstVisitor
	{
		#region Properties
		bool halt; 
		public IBlockNode scopedBlock;
		public IStatement scopedStatement;

		bool explicitlyNoCompletion;
		public AbstractCompletionProvider GeneratedProvider { 
			get{ 
				return prv ?? (explicitlyNoCompletion ? null : 
					new CtrlSpaceCompletionProvider(cdgen) { curBlock = scopedBlock, curStmt = scopedStatement }); 
			} 
		}
		AbstractCompletionProvider prv;
		readonly ICompletionDataGenerator cdgen;
		#endregion

		public CompletionProviderVisitor(ICompletionDataGenerator cdg)
		{
			this.cdgen = cdg;
		}

		#region Nodes
		public override void VisitDNode (DNode n)
		{
			if (n.NameHash == DTokens.IncompleteIdHash) {
				explicitlyNoCompletion = true;
				halt = true;
			}
			else
				base.VisitDNode (n);
		}
		#endregion

		#region Attributes
		public override void VisitAttribute (Modifier a)
		{
			if (a.ContentHash == DTokens.IncompleteIdHash) {
				prv = new AttributeCompletionProvider (cdgen) { Attribute = a };
				halt = true;
			}
			else
				base.VisitAttribute (a);
		}

		public override void Visit (ScopeGuardStatement s)
		{
			if (s.GuardedScope == DTokens.IncompleteId) {
				prv = new ScopeAttributeCompletionProvider (cdgen);
				halt = true;
			}
			else
				base.Visit (s);
		}

		public override void VisitAttribute (PragmaAttribute a)
		{
			if (a.Arguments != null && 
				a.Arguments.Length>0 &&
				IsIncompleteExpression (a.Arguments[a.Arguments.Length-1])) {
				prv = new AttributeCompletionProvider (cdgen) { Attribute=a };
				halt = true;
			}
			else
				base.VisitAttribute (a);
		}

		public override void VisitAttribute (UserDeclarationAttribute a)
		{
			if (a.AttributeExpression != null && 
				a.AttributeExpression.Length>0 &&
				IsIncompleteExpression (a.AttributeExpression[0])) {
				prv = new PropertyAttributeCompletionProvider (cdgen);
				halt = true;
			}
			else
				base.VisitAttribute (a);
		}
		#endregion

		#region Statements

		#endregion

		#region Expressions
		public override void VisitChildren (ContainerExpression x)
		{
			if(!halt)
				base.VisitChildren (x);
		}

		public override void Visit (TokenExpression e)
		{
			if (e.Token == DTokens.Incomplete) {
				halt = true;
			}
		}

		static bool IsIncompleteExpression(IExpression x)
		{
			return x is TokenExpression && (x as TokenExpression).Token == DTokens.Incomplete;
		}

		public override void Visit (PostfixExpression_Access x)
		{
			if (IsIncompleteExpression(x.AccessExpression)) {
				halt = true;
				prv = new MemberCompletionProvider (cdgen) { 
					AccessExpression = x as PostfixExpression_Access, 
					ScopedBlock = scopedBlock, 
					ScopedStatement = scopedStatement };
			}
			else
				base.Visit (x);
		}

		public override void Visit (TraitsExpression x)
		{
			if(x.Arguments != null && x.Arguments.Length > 0 && 
				IsIncompleteExpression(x.Arguments[x.Arguments.Length-1].AssignExpression))
			{
				prv = new TraitsExpressionCompletionProvider(cdgen);
				halt = true;
			}
			else
				base.Visit (x);
		}
		#endregion
	}
}

