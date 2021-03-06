//
// MemberFilter.cs
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

namespace D_Parser.Resolver.ASTScanner
{
	/// <summary>
	/// A whitelisting filter for members to show in completion menus.
	/// </summary>
	[Flags]
	public enum MemberFilter
	{
		None = 0,
		Variables = 1,
		Methods = 1 << 2,
		Classes = 1 << 3,
		Interfaces = 1 << 4,
		Templates = 1 << 5,
		StructsAndUnions = 1 << 6,
		Enums = 1 << 7,
		// 1 << 8 -- see BlockKeywords
		TypeParameters = 1 << 9,
		Labels = 1 << 10,
		x86Registers = 1 << 11,
		x64Registers = 1 << 12,
		BuiltInPropertyAttributes = 1 << 13,

		BlockKeywords = 1 << 8, // class, uint, __gshared, [static] if, (body,in,out)
		StatementBlockKeywords = 1 << 14, // for, if, 
		ExpressionKeywords = 1 << 15, // __LINE__, true, base, this


		Registers = x86Registers | x64Registers,
		Types = Classes | Interfaces | Templates | StructsAndUnions,
		All = Variables | Methods | Types | Enums | TypeParameters
	}
}

