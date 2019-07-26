﻿namespace Ceras.Helpers
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;
	using System.Runtime.CompilerServices;
	using System.Runtime.InteropServices;

	static class ReflectionHelper
	{
		static object ReturnNull() => null;
		internal static readonly Func<object> _nullResultDelegate = (Func<object>)ReturnNull;
		static readonly Dictionary<Type, int> _typeToUnsafeSize = new Dictionary<Type, int>();
		

		static ReflectionHelper()
		{
			_typeToUnsafeSize[typeof(Byte)] = 1;
			_typeToUnsafeSize[typeof(SByte)] = 1;
			_typeToUnsafeSize[typeof(Boolean)] = 1;
			
			_typeToUnsafeSize[typeof(Int16)] = 2;
			_typeToUnsafeSize[typeof(UInt16)] = 2;
			_typeToUnsafeSize[typeof(Char)] = 2;
			
			_typeToUnsafeSize[typeof(Int32)] = 4;
			_typeToUnsafeSize[typeof(UInt32)] = 4;
			_typeToUnsafeSize[typeof(Single)] = 4;
			
			_typeToUnsafeSize[typeof(Int64)] = 8;
			_typeToUnsafeSize[typeof(UInt64)] = 8;
			_typeToUnsafeSize[typeof(Double)] = 8;
		}


		public static Type FindClosedType(Type type, Type openGeneric)
		{
			if (openGeneric.IsInterface)
			{
				var collectionInterfaces = type.FindInterfaces((f, o) =>
				{
					if (!f.IsGenericType)
						return false;
					return f.GetGenericTypeDefinition() == openGeneric;
				}, null);

				// In the case of interfaces, it can not only be the case where an interface inherits another interface, 
				// but there can also be the case where the interface itself is already the type we are looking for!
				if (type.IsGenericType)
					if (type.GetGenericTypeDefinition() == openGeneric)
						return type;

				if (collectionInterfaces.Length > 0)
				{
					return collectionInterfaces[0];
				}
			}
			else
			{
				// Go up through the hierarchy until we find that open generic
				var t = type;
				while (t != null)
				{
					if (t.IsGenericType)
						if (t.GetGenericTypeDefinition() == openGeneric)
							return t;

					t = t.BaseType;
				}
			}


			return null;
		}

		public static Type FindClosedArg(this Type objectType, Type openGeneric, int argIndex = 0)
		{
			var closed = FindClosedType(objectType, openGeneric);
			if (closed == null)
				return null;
			var args = closed.GetGenericArguments();
			return args[argIndex];
		}

		public static bool IsAssignableToGenericType(Type givenType, Type genericType)
		{
			if (genericType.IsAssignableFrom(givenType))
				return true;

			var interfaceTypes = givenType.GetInterfaces();

			foreach (var it in interfaceTypes)
			{
				// Ok, we lied, we also allow it if 'genericType' is an interface and 'givenType' implements it
				if (it == genericType)
					return true;

				if (it.IsGenericType && it.GetGenericTypeDefinition() == genericType)
					return true;
			}

			if (givenType.IsGenericType && givenType.GetGenericTypeDefinition() == genericType)
				return true;

			Type baseType = givenType.BaseType;
			if (baseType == null)
				return false;

			return IsAssignableToGenericType(baseType, genericType);
		}


		public static IEnumerable<MemberInfo> GetAllDataMembers(this Type type, bool fields = true, bool properties = true)
		{
			if (type.IsPrimitive)
				yield break;

			foreach (var m in EnumerateMembers(type))
			{
				if (m is FieldInfo f && fields && !f.IsStatic)
					yield return m;
				if (m is PropertyInfo p && properties && !p.GetAccessors(true)[0].IsStatic)
					yield return m;
			}
		}

		public static IEnumerable<MemberInfo> GetAllStaticDataMembers(this Type type, bool fields = true, bool properties = true)
		{
			if (type.IsPrimitive)
				yield break;

			foreach (var m in EnumerateMembers(type))
			{
				if (m is FieldInfo f && fields && f.IsStatic)
					yield return m;
				if (m is PropertyInfo p && properties && p.GetAccessors(true)[0].IsStatic)
					yield return m;
			}
		}

		static IEnumerable<MemberInfo> EnumerateMembers(this Type type)
		{
			var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

			while (type != null)
			{
				foreach (var f in type.GetFields(flags))
					if (f.DeclaringType == type)
						yield return f;

				foreach (var p in type.GetProperties(flags))
					if (p.DeclaringType == type)
					{
						var getter = p.GetGetMethod(true);
						if (getter != null)
							if (getter.GetParameters().Length > 0)
								// Indexers are not data
								continue;

						yield return p;
					}

				type = type.BaseType;
			}
		}


		public static bool IsBlittableType(Type type)
		{
			if (!type.IsValueType)
				return false;

			if (type.IsEnum)
				return true; // enums are auto-layout, but they're always blittable

			if (type.IsPointer || type == typeof(IntPtr) || type == typeof(UIntPtr))
				return false; // pointers are primitive, but we can never blit them (because they can be a different size)

			if (type.IsPrimitive)
				return true; // any other primitive can definitely be blitted


			if (type.ContainsGenericParameters)
				return false; // open types are not real (yet), so they can't be blitted

			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
				return false; // we want to have explicit control over nullables

			if (type.IsAutoLayout)
				return false; // generic structs are always auto-layout (since you can't know what layout is best before inserting the generic args)


			foreach (var f in GetAllDataMembers(type, true, false).Cast<FieldInfo>())
			{
				var fixedBuffer = f.GetCustomAttribute<FixedBufferAttribute>();
				if (fixedBuffer != null)
				{
					//var innerType = ;

					continue;
				}

				if (f.GetCustomAttribute<MarshalAsAttribute>() != null)
				{
					throw new NotSupportedException("The [MarshalAs] attribute is not supported");
				}

				if (!IsBlittableType(f.FieldType))
					return false;
			}

			return true;
		}

		public static int GetSize(Type type)
		{
			if(!type.IsValueType)
				throw new InvalidOperationException($"Cannot get size of type '{type.FriendlyName()}'");

			lock (_typeToUnsafeSize)
			{
				if (_typeToUnsafeSize.TryGetValue(type, out int size))
					return size;

				var m = typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf)).MakeGenericMethod(type);
				size = (int)m.Invoke(null, null);

				_typeToUnsafeSize.Add(type, size);
				return size;
			}
		}
		

		public static Type FieldOrPropType(this MemberInfo memberInfo)
		{
			if (memberInfo is FieldInfo f)
				return f.FieldType;
			if (memberInfo is PropertyInfo p)
				return p.PropertyType;
			throw new InvalidOperationException();
		}


		/// <summary>
		/// Find the MethodInfo that matches the given name and specific parameters on the given type. 
		/// All parameter types must be "closed" (not contain any unspecified generic parameters). 
		/// </summary>
		/// <param name="declaringType">The type in which to look for the method</param>
		/// <param name="name">The name of the method (method group) to find</param>
		/// <param name="parameters">The specific parameters</param>
		public static MethodInfo ResolveMethod(Type declaringType, string name, Type[] parameters)
		{
			var methods = declaringType
						  .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
						  .Where(m => m.Name == name &&
									  m.GetParameters().Length == parameters.Length)
						  .ToArray();

			var match = SelectMethod(methods, parameters);
			return match;
		}

		// Select exactly one method out of the given methods that matches the specific (closed) arguments
		static MethodInfo SelectMethod(MethodInfo[] methods, Type[] specificArguments)
		{
			var matches = new List<MethodInfo>();

			foreach (var m in methods)
			{
				if (m.IsGenericMethod)
				{
					// Inspect generic arguments and parameters in depth
					var closedMethod = TryCloseOpenGeneric(m, specificArguments);
					if (closedMethod != null)
						matches.Add(closedMethod);
				}
				else
				{
					// Just check if the parameters match
					var parameters = m.GetParameters();

					bool allMatch = true;
					for (int argIndex = 0; argIndex < specificArguments.Length; argIndex++)
					{
						var pType = parameters[argIndex].ParameterType;
						var argType = specificArguments[argIndex];

						if (pType != argType)
						// if (!pType.IsAssignableFrom(argType))
						{
							allMatch = false;
							break;
						}
					}

					if (allMatch)
						matches.Add(m);
				}
			}

			if (matches.Count == 0)
				return null;

			if (matches.Count > 1)
				throw new AmbiguousMatchException("The given parameters can match more than one method overload.");

			return matches[0];
		}

		// Try to close an open generic method, making it take the given argument types
		public static MethodInfo TryCloseOpenGeneric(MethodInfo openGenericMethod, Type[] specificArguments)
		{
			if (!openGenericMethod.IsGenericMethodDefinition)
				throw new ArgumentException($"'{nameof(openGenericMethod)}' must be a generic method definition");

			foreach (var t in specificArguments)
				if (t.ContainsGenericParameters)
					throw new InvalidOperationException($"Can't close open generic method '{openGenericMethod}' At least one of the given argument types is not fully closed: '{t.FullName}'");

			var parameters = openGenericMethod.GetParameters();

			// Go through the parameters recursively, once we find a generic parameter (or some nested one) then we can infer the type from the given specific arguments.
			// If that is the first time we encounter this generic parameter, then that establishes what specific type it actually is.
			// If we have already seen this generic parameter, we check if it matches. If not, we can immediately conclude that the method won't match.
			// When we reach the end, we know that the method is a match.

			// - While establishing the generic arguments we can check the generic constraints
			// - We can also check if parameters could still fit even if they're not an exact match (an more derived argument going into a base-type parameter)

			var genArgToConcreteType = new Dictionary<Type, Type>();

			var genArgs = openGenericMethod.GetGenericArguments();

			for (int paramIndex = 0; paramIndex < parameters.Length; paramIndex++)
			{
				var p = parameters[paramIndex];
				var arg = specificArguments[paramIndex];

				// For now we only accept exact matches. We won't make any assumptions about being able to pass in more derived types.
				if (IsParameterMatch(p.ParameterType, arg, genArgToConcreteType, genArgs, true))
				{
					// Ok, at least this parameter matches, try the others...
				}
				else
				{
					return null;
				}
			}

			// All the parameters seem to match, let's do a final check to see if everything really matches up
			// Lets extract the genericTypeParameters from the dictionary
			var closedGenericArgs = genArgs.OrderBy(g => g.GenericParameterPosition).Select(g => genArgToConcreteType[g]).ToArray();

			// Try to instantiate the method using those args
			try
			{
				var instantiatedGenericMethod = openGenericMethod.MakeGenericMethod(closedGenericArgs);

				// Ok, do the parameter types match perfectly?
				var createdParams = instantiatedGenericMethod.GetParameters();

				for (int i = 0; i < createdParams.Length; i++)
					if (createdParams[i].ParameterType != specificArguments[i])
						// Somehow, after instantiating the method with these parameters, something doesn't match up...
						return null;


				return instantiatedGenericMethod;
			}
			catch
			{
				// Can't instantiate generic with those type-arguments
				return null;
			}
		}

		// Check if the given argument type 'arg' matches the parameter 'parameterType'.
		// Use 'genArgToConcreteType' to either lookup any already specified generic arguments, or infer and define them!
		// 'methodGenericArgs' just contains the "undefined" generic argument types to be used as the key in the dictionary.
		static bool IsParameterMatch(Type parameterType, Type arg, Dictionary<Type, Type> genArgToConcreteType, Type[] methodGenericArgs, bool mustMatchExactly)
		{
			// Direct match?
			//  "typeof(int) == typeof(int)"
			if (mustMatchExactly)
			{
				if (parameterType == arg)
					return true;
			}
			else
			{
				if (parameterType.IsAssignableFrom(arg))
					return true;
			}

			// Is it a generic parameter '<T>'?
			var genericArg = methodGenericArgs.FirstOrDefault(g => g == parameterType);
			if (genericArg != null)
			{
				// The parameter is a '<T>'
				if (genArgToConcreteType.TryGetValue(genericArg, out var existingGenericTypeArgument))
				{
					if (existingGenericTypeArgument != arg)
					{
						// Signature does not match
						return false;
					}
					else
					{
						// The arg matches the already established one
					}
				}
				else
				{
					// Establish that this generic type argument will be 'arg'
					genArgToConcreteType.Add(genericArg, arg);
				}

				return true;
			}

			// If both of them are generics, we have to open them up
			if (parameterType.IsGenericType && arg.IsGenericType)
			{
				var paramGenericTypeDef = parameterType.GetGenericTypeDefinition();
				var argGenericTypeDef = arg.GetGenericTypeDefinition();

				if (paramGenericTypeDef != argGenericTypeDef)
					// "Containers" are not compatible
					return false;

				// Open up the containers and inspect each generic argument
				var specificGenArgs = arg.GetGenericArguments();
				var genArgs = parameterType.GetGenericArguments();

				for (int i = 0; i < genArgs.Length; i++)
				{
					if (!IsParameterMatch(genArgs[i], specificGenArgs[i], genArgToConcreteType, methodGenericArgs, mustMatchExactly))
						return false;
				}

				return true;
			}


			// Types don't match, and are not compatible generics
			return false;
		}



		internal static MethodInfo GetMethod(Expression<Action> e)
		{
			var b = e.Body;

			if (b is MethodCallExpression m)
				return m.Method;

			throw new ArgumentException();
		}

		internal static MethodInfo GetMethod<T>(Expression<Func<T>> e)
		{
			var b = e.Body;

			if (b is MethodCallExpression m)
				return m.Method;

			throw new ArgumentException();
		}

		internal static bool IsStatic(this Type type)
		{
			return type.IsAbstract && type.IsSealed;
		}

		internal static bool IsAbstract(this Type type)
		{
			return type.IsAbstract && !type.IsSealed;
		}

		public static string FriendlyName(this Type type, bool fullName = false)
		{
			if (type == typeof(bool))  return "bool";

			if (type == typeof(byte))  return "byte";
			if (type == typeof(sbyte))  return "sbyte";			
			
			if (type == typeof(short)) return "short";
			if (type == typeof(ushort)) return "ushort";

			if (type == typeof(int))	return "int";
			if (type == typeof(uint))	return "uint";
			
			if (type == typeof(long))  return "long";
			if (type == typeof(ulong))  return "ulong";

			if (type == typeof(float)) return "float";
			if (type == typeof(double)) return "double";

			if (type == typeof(decimal)) return "decimal";
			
			if (type == typeof(string)) return "string";
			if (type == typeof(char)) return "char";
			
			if (type.IsGenericType)
			{
				var n = fullName ? type.FullName : type.Name;
				return n.Split('`')[0] + "<" + string.Join(", ", type.GetGenericArguments().Select(t => t.FriendlyName(fullName)).ToArray()) + ">";
			}
			else
			{
				return fullName ? type.FullName : type.Name;
			}
		}
	}
	
	// This is here so we are able to get specific internal framework types.
	// Such as "System.RtFieldInfo" or "System.RuntimeTypeInfo", ...
	static class MemberHelper
	{
		// Helper members
		internal static byte _field;
		internal static byte _prop { get; set; }
		internal static void _method() { }
	}
}