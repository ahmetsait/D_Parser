﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using D_Parser.Dom.Expressions;
using D_Parser.Dom;

namespace D_Parser.Resolver.ExpressionSemantics
{
	/// <summary>
	/// An expression value that is allowed to have a new value assigned to as in 'a = b;'
	/// </summary>
	public abstract class LValue : ExpressionValue
	{
		public LValue(AbstractType nodeType) : base(nodeType) { }

		public abstract void Set(AbstractSymbolValueProvider vp, ISymbolValue value);
	}

	/// <summary>
	/// Contains a reference to a DVariable node.
	/// To get the actual value of the variable, use the value provider.
	/// </summary>
	public class VariableValue : LValue
	{
		public readonly DVariable Variable;

		public VariableValue(DVariable variable, AbstractType variableType) : base(variableType)
		{
			this.Variable = variable;
		}

		public override void Set(AbstractSymbolValueProvider vp, ISymbolValue value)
		{
			vp[Variable] = value;
		}

		public override string ToCode()
		{
			return Variable==null ?"null":Variable.ToString(false);
		}
	}

	public class StaticVariableValue : VariableValue
	{
		public StaticVariableValue(DVariable artificialVariable, AbstractType propType)
			: base(artificialVariable, propType) { }

		public override void Set(AbstractSymbolValueProvider vp, ISymbolValue value)
		{
			throw new EvaluationException("Cannot assign a value to a static property.", this, value);
			//TODO: What about array.length?
		}
	}

	/// <summary>
	/// Used for accessing entries from an array.
	/// </summary>
	public class ArrayPointer : VariableValue
	{
		/// <summary>
		/// Used when accessing normal arrays.
		/// If -1, a item passed to Set() will be added instead of replaced.
		/// </summary>
		public readonly int ItemNumber;

		public override void Set(AbstractSymbolValueProvider vp, ISymbolValue value)
		{
			var oldV = vp[Variable];

			if (oldV is ArrayValue)
			{
				var av = (ArrayValue)oldV;

				//TODO: Immutability checks

				if (av.IsString)
				{

				}
				else
				{
					var at = av.RepresentedType as ArrayType;
					var newElements = new ISymbolValue[av.Elements.Length + (ItemNumber<0 ? 1:0)];
					av.Elements.CopyTo(newElements, 0);

					if (!ResultComparer.IsImplicitlyConvertible(value.RepresentedType, at.ValueType))
						throw new EvaluationException(value.ToCode() + " must be implicitly convertible to the array's value type!", value);

					// Add..
					if (ItemNumber < 0)
						av.Elements[av.Elements.Length - 1] = value;
					else // or set the new value
						av.Elements[ItemNumber] = value;

					vp[Variable] = new ArrayValue(at, newElements);
				}
			}
			else
				throw new EvaluationException("Type of accessed item must be an array", oldV);
		}

		/// <summary>
		/// Array ctor.
		/// </summary>
		/// <param name="accessedItem">0 - the array's length-1; -1 when adding the item is wished.</param>
		public ArrayPointer(DVariable accessedArray, ArrayType arrayType, int accessedItem)
			: base(accessedArray, arrayType)
		{
			ItemNumber = accessedItem;
		}
	}

	public class AssocArrayPointer : VariableValue
	{
		/// <summary>
		/// Used to identify the accessed item.
		/// </summary>
		public readonly ISymbolValue Key;
		
		public AssocArrayPointer(DVariable accessedArray, AssocArrayType arrayType, ISymbolValue accessedItemKey)
			: base(accessedArray, arrayType)
		{
			Key = accessedItemKey;
		}

		public override void Set(AbstractSymbolValueProvider vp, ISymbolValue value)
		{
			var oldV = vp[Variable];

			if (oldV is AssociativeArrayValue)
			{
				if (Key != null)
				{
					var aa = (AssociativeArrayValue)oldV;

					int itemToReplace = -1;

					for (int i = 0; i < aa.Elements.Count; i++)
						if (SymbolValueComparer.IsEqual(aa.Elements[i].Key, Key))
						{
							itemToReplace = i;
							break;
						}

					// If we haven't found a matching key, add it to the array
					var newElements = new KeyValuePair<ISymbolValue, ISymbolValue>[aa.Elements.Count + (itemToReplace == -1 ? 1 : 0)];
					aa.Elements.CopyTo(newElements, 0);

					if (itemToReplace != -1)
						newElements[itemToReplace] = new KeyValuePair<ISymbolValue, ISymbolValue>(newElements[itemToReplace].Key, value);
					else
						newElements[newElements.Length - 1] = new KeyValuePair<ISymbolValue, ISymbolValue>(Key, value);

					// Finally, make a new associative array containing the new elements
					vp[Variable] = new AssociativeArrayValue(aa.RepresentedType as AssocArrayType, newElements);
				}
				else
					throw new EvaluationException("Key expression must not be null", Key);
			}
			else
				throw new EvaluationException("Type of accessed item must be an associative array", oldV);
		}
	}

	/*public class PointerValue : ExpressionValue
	{

	}*/
}
