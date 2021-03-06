/*
  Copyright (C) 2007 Jeroen Frijters

  This software is provided 'as-is', without any express or implied
  warranty.  In no event will the authors be held liable for any damages
  arising from the use of this software.

  Permission is granted to anyone to use this software for any purpose,
  including commercial applications, and to alter it and redistribute it
  freely, subject to the following restrictions:

  1. The origin of this software must not be misrepresented; you must not
     claim that you wrote the original software. If you use this software
     in a product, an acknowledgment in the product documentation would be
     appreciated but is not required.
  2. Altered source versions must be plainly marked as such, and must not be
     misrepresented as being the original software.
  3. This notice may not be removed or altered from any source distribution.

  Jeroen Frijters
  jeroen@frijters.net
  
*/

using System;
using System.Collections.Generic;
#if STATIC_COMPILER
using IKVM.Reflection;
using IKVM.Reflection.Emit;
using Type = IKVM.Reflection.Type;
#else
using System.Reflection;
using System.Reflection.Emit;
#endif
using IKVM.Internal;
using InstructionFlags = IKVM.Internal.ClassFile.Method.InstructionFlags;

static class AtomicReferenceFieldUpdaterEmitter
{
	private static readonly Dictionary<FieldWrapper, ConstructorBuilder> map = new Dictionary<FieldWrapper, ConstructorBuilder>();

	internal static bool Emit(DynamicTypeWrapper.FinishContext context, TypeWrapper wrapper, CodeEmitter ilgen, ClassFile classFile, int i, ClassFile.Method.Instruction[] code, InstructionFlags[] flags)
	{
		if (i >= 3
			&& (flags[i - 0] & InstructionFlags.BranchTarget) == 0
			&& (flags[i - 1] & InstructionFlags.BranchTarget) == 0
			&& (flags[i - 2] & InstructionFlags.BranchTarget) == 0
			&& (flags[i - 3] & InstructionFlags.BranchTarget) == 0
			&& code[i - 1].NormalizedOpCode == NormalizedByteCode.__ldc
			&& code[i - 2].NormalizedOpCode == NormalizedByteCode.__ldc
			&& code[i - 3].NormalizedOpCode == NormalizedByteCode.__ldc)
		{
			// we now have a structural match, now we need to make sure that the argument values are what we expect
			TypeWrapper tclass = classFile.GetConstantPoolClassType(code[i - 3].Arg1);
			TypeWrapper vclass = classFile.GetConstantPoolClassType(code[i - 2].Arg1);
			string fieldName = classFile.GetConstantPoolConstantString(code[i - 1].Arg1);
			if (tclass == wrapper && !vclass.IsUnloadable && !vclass.IsPrimitive && !vclass.IsNonPrimitiveValueType)
			{
				FieldWrapper field = wrapper.GetFieldWrapper(fieldName, vclass.SigName);
				if (field != null && !field.IsStatic && field.IsVolatile && field.DeclaringType == wrapper && field.FieldTypeWrapper == vclass)
				{
					// everything matches up, now call the actual emitter
					DoEmit(context, wrapper, ilgen, field);
					return true;
				}
			}
		}
		return false;
	}

	private static void DoEmit(DynamicTypeWrapper.FinishContext context, TypeWrapper wrapper, CodeEmitter ilgen, FieldWrapper field)
	{
		ConstructorBuilder cb;
		bool exists;
		lock (map)
		{
			exists = map.TryGetValue(field, out cb);
		}
		if (!exists)
		{
			// note that we don't need to lock here, because we're running as part of FinishCore, which is already protected by a lock
			TypeWrapper arfuTypeWrapper = ClassLoaderWrapper.LoadClassCritical("ikvm.internal.IntrinsicAtomicReferenceFieldUpdater");
			TypeBuilder tb = wrapper.TypeAsBuilder.DefineNestedType("__<ARFU>_" + field.Name + field.Signature.Replace('.', '/'), TypeAttributes.NestedPrivate | TypeAttributes.Sealed, arfuTypeWrapper.TypeAsBaseType);
			EmitCompareAndSet("compareAndSet", tb, field.GetField());
			EmitGet(tb, field.GetField());
			EmitSet("set", tb, field.GetField());

			cb = tb.DefineConstructor(MethodAttributes.Assembly, CallingConventions.Standard, Type.EmptyTypes);
			lock (map)
			{
				map.Add(field, cb);
			}
			CodeEmitter ctorilgen = CodeEmitter.Create(cb);
			ctorilgen.Emit(OpCodes.Ldarg_0);
			MethodWrapper basector = arfuTypeWrapper.GetMethodWrapper("<init>", "()V", false);
			basector.Link();
			basector.EmitCall(ctorilgen);
			ctorilgen.Emit(OpCodes.Ret);
			ctorilgen.DoEmit();
			context.RegisterPostFinishProc(delegate
			{
				arfuTypeWrapper.Finish();
				tb.CreateType();
			});
		}
		ilgen.Emit(OpCodes.Pop);
		ilgen.Emit(OpCodes.Pop);
		ilgen.Emit(OpCodes.Pop);
		ilgen.Emit(OpCodes.Newobj, cb);
	}

	private static void EmitCompareAndSet(string name, TypeBuilder tb, FieldInfo field)
	{
		MethodBuilder compareAndSet = tb.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Virtual, Types.Boolean, new Type[] { Types.Object, Types.Object, Types.Object });
		ILGenerator ilgen = compareAndSet.GetILGenerator();
		ilgen.Emit(OpCodes.Ldarg_1);
		ilgen.Emit(OpCodes.Castclass, field.DeclaringType);
		ilgen.Emit(OpCodes.Ldflda, field);
		ilgen.Emit(OpCodes.Ldarg_3);
		ilgen.Emit(OpCodes.Castclass, field.FieldType);
		ilgen.Emit(OpCodes.Ldarg_2);
		ilgen.Emit(OpCodes.Castclass, field.FieldType);
		ilgen.Emit(OpCodes.Call, MakeCompareExchange(field.FieldType));
		ilgen.Emit(OpCodes.Ldarg_2);
		ilgen.Emit(OpCodes.Ceq);
		ilgen.Emit(OpCodes.Ret);
	}

	private static MethodInfo MakeCompareExchange(Type type)
	{
		MethodInfo interlockedCompareExchange = null;
		foreach (MethodInfo m in JVM.Import(typeof(System.Threading.Interlocked)).GetMethods())
		{
			if (m.Name == "CompareExchange" && m.IsGenericMethodDefinition)
			{
				interlockedCompareExchange = m;
				break;
			}
		}
		return interlockedCompareExchange.MakeGenericMethod(type);
	}

	private static void EmitGet(TypeBuilder tb, FieldInfo field)
	{
		MethodBuilder get = tb.DefineMethod("get", MethodAttributes.Public | MethodAttributes.Virtual, Types.Object, new Type[] { Types.Object });
		ILGenerator ilgen = get.GetILGenerator();
		ilgen.Emit(OpCodes.Ldarg_1);
		ilgen.Emit(OpCodes.Castclass, field.DeclaringType);
		ilgen.Emit(OpCodes.Volatile);
		ilgen.Emit(OpCodes.Ldfld, field);
		ilgen.Emit(OpCodes.Ret);
	}

	private static void EmitSet(string name, TypeBuilder tb, FieldInfo field)
	{
		MethodBuilder set = tb.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Virtual, Types.Void, new Type[] { Types.Object, Types.Object });
		CodeEmitter ilgen = CodeEmitter.Create(set);
		ilgen.Emit(OpCodes.Ldarg_1);
		ilgen.Emit(OpCodes.Castclass, field.DeclaringType);
		ilgen.Emit(OpCodes.Ldarg_2);
		ilgen.Emit(OpCodes.Castclass, field.FieldType);
		ilgen.Emit(OpCodes.Volatile);
		ilgen.Emit(OpCodes.Stfld, field);
		ilgen.EmitMemoryBarrier();
		ilgen.Emit(OpCodes.Ret);
		ilgen.DoEmit();
	}
}
