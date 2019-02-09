﻿using System;

namespace LiveTesting
{
	using BenchmarkDotNet.Running;
	using Ceras;
	using Ceras.Formatters;
	using Ceras.Resolvers;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Numerics;
	using System.Reflection;
	using System.Text;
	using Tutorial;
	using Xunit;

	class Program
	{
		static Guid staticGuid = Guid.Parse("39b29409-880f-42a4-a4ae-2752d97886fa");
		delegate void ActionDelegate();

		static void Main(string[] args)
		{
			Benchmarks();

			ExpressionTreesTest();

			TestDirectPoolingMethods();

			DelegatesTest();

			TuplesTest();

			EnsureSealedTypesThrowsException();

			InjectSpecificFormatterTest();

			BigIntegerTest();

			VersionToleranceTest();

			ReadonlyTest();

			MemberInfoAndTypeInfoTest();

			SimpleDictionaryTest();

			DictInObjArrayTest();

			MaintainTypeTest();

			InterfaceFormatterTest();

			InheritTest();

			StructTest();

			WrongRefTypeTest();

			PerfTest();

			TupleTest();

			NullableTest();

			ErrorOnDirectEnumerable();

			CtorTest();

			PropertyTest();

			NetworkTest();

			GuidTest();

			EnumTest();

			ComplexTest();

			ListTest();



			var tutorial = new Tutorial();

			tutorial.Step1_SimpleUsage();
			tutorial.Step2_Attributes();
			tutorial.Step3_Recycling();
			tutorial.Step4_KnownTypes();
			tutorial.Step5_CustomFormatters();
			// tutorial.Step6_NetworkExample();
			tutorial.Step7_GameDatabase();
			// tutorial.Step8_DataUpgrade_OLD();
			// tutorial.Step9_VersionTolerance();
			tutorial.Step10_ReadonlyHandling();

			Console.WriteLine("All tests completed.");
			Console.ReadKey();
		}


		class ReadonlyTestClass
		{
			readonly string _name = "default";

			public ReadonlyTestClass(string name)
			{
				_name = name;
			}

			public string GetName()
			{
				return _name;
			}
		}

		static void ExpressionTreesTest()
		{
			Expression<Func<string, int, char>> getCharAtIndex = (text, index) => (text.ElementAt(index).ToString() + text[index])[0];

			var del = getCharAtIndex.Compile();

			string inputString = "abcde";
			char c1 = del(inputString, 2);


			// Serialize and deserialize delegate
			SerializerConfig config = new SerializerConfig();

			ExpressionFormatterResolver.Configure(config);


			var ceras = new CerasSerializer(config);

			var data = ceras.Serialize<object>(getCharAtIndex);
			var dataAsStr = Encoding.ASCII.GetString(data).Replace('\0', ' ');

			var clonedExp = (Expression<Func<string, int, char>>)ceras.Deserialize<object>(data);

			var del2 = clonedExp.Compile();
			var c2 = del2(inputString, 2);

			Console.WriteLine();

			// Can we make an expression to accelerate writing to readonly fields?
			/*
			{
				var p = new ReadonlyTestClass("abc");

				var f = p.GetType().GetField("_name", BindingFlags.Instance | BindingFlags.NonPublic);
				var objParam = Expression.Parameter(p.GetType(), "obj");
				var newValParam = Expression.Parameter(typeof(string), "newVal");

				var fieldExp = Expression.Field(objParam, f);
				// var assignment = Expression.Assign(fieldExp, newValParam);

				var assignmentOp = typeof(Expression).Assembly.GetType("System.Linq.Expressions.AssignBinaryExpression");
				var assignment = (Expression)Activator.CreateInstance(assignmentOp,
																	  BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.CreateInstance,
																	  null,
																	  new object[] { fieldExp, newValParam },
																	  null);

				// Create the method

				DynamicMethod dynamicMethod = new DynamicMethod("test", null, new Type[] { typeof(ReadonlyTestClass), typeof(string) }, owner: typeof(ReadonlyTestClass), skipVisibility: true);
				var ilGen = dynamicMethod.GetILGenerator();

				MethodBuilder methodBuilder = ;
				Expression.Lambda<Action<ReadonlyTestClass, string>>(assignment, objParam, newValParam).CompileToMethod(methodBuilder);

				del(p, "changed!");
			}
			*/
		}


		class Person
		{
			public const string CtorSuffix = " (modified by constructor)";

			public string Name;
			public int Health;
			public Person BestFriend;

			public Person()
			{
			}

			public Person(string name)
			{
				Name = name + CtorSuffix;
			}
		}

		static void TestDirectPoolingMethods()
		{
			var pool = new InstancePoolTest();

			// Test: Ctor with argument
			{
				SerializerConfig config = new SerializerConfig();

				config.ConfigType<Person>()
					  // Select ctor, not delegate
					  .ConstructBy(() => new Person("name"));

				var clone = DoRoundTripTest(config);
				Debug.Assert(clone != null);
				Debug.Assert(clone.Name.StartsWith("riki"));
				Debug.Assert(clone.Name.EndsWith(Person.CtorSuffix));
			}

			// Test: Manual config
			{
				SerializerConfig config = new SerializerConfig();

				config.ConfigType<Person>()
					  .ConstructBy(TypeConstruction.ByStaticMethod(() => StaticPoolTest.CreatePerson()));

				var clone = DoRoundTripTest(config);
				Debug.Assert(clone != null);
			}

			// Test: Normal ctor, but explicitly
			{
				SerializerConfig config = new SerializerConfig();

				config.ConfigType<Person>()
					  // Select ctor, not delegate
					  .ConstructBy(() => new Person());

				var clone = DoRoundTripTest(config);
				Debug.Assert(clone != null);
			}

			// Test: Construct from instance-pool
			{
				SerializerConfig config = new SerializerConfig();

				config.ConfigType<Person>()
					  // Instance + method select
					  .ConstructBy(pool, () => pool.CreatePerson());

				var clone = DoRoundTripTest(config);
				Debug.Assert(clone != null);
				Debug.Assert(pool.IsFromPool(clone));
			}

			// Test: Construct from static-pool
			{
				SerializerConfig config = new SerializerConfig();

				config.ConfigType<Person>()
					  // method select
					  .ConstructBy(() => StaticPoolTest.CreatePerson());

				var clone = DoRoundTripTest(config);
				Debug.Assert(clone != null);
				Debug.Assert(StaticPoolTest.IsFromPool(clone));
			}

			// Test: Construct from any delegate (in this example: a lambda expression)
			{
				SerializerConfig config = new SerializerConfig();

				Person referenceCapturedByLambda = null;

				config.ConfigType<Person>()
					  // Use delegate
					  .ConstructByDelegate(() =>
					  {
						  var obj = new Person();
						  referenceCapturedByLambda = obj;
						  return obj;
					  });

				var clone = DoRoundTripTest(config);
				Debug.Assert(clone != null);
				Debug.Assert(ReferenceEquals(clone, referenceCapturedByLambda));
			}

			// Test: Construct from instance-pool, with parameter
			{
				SerializerConfig config = new SerializerConfig();

				config.ConfigType<Person>()
					  // Use instance + method selection
					  .ConstructBy(pool, () => pool.CreatePersonWithName("abc"));

				var clone = DoRoundTripTest(config);
				Debug.Assert(clone != null);
				Debug.Assert(clone.Name.StartsWith("riki"));
				Debug.Assert(pool.IsFromPool(clone));
			}

			// Test: Construct from static-pool, with parameter
			{
				SerializerConfig config = new SerializerConfig();

				config.ConfigType<Person>()
					  // Use instance + method selection
					  .ConstructBy(() => StaticPoolTest.CreatePersonWithName("abc"));

				var clone = DoRoundTripTest(config);
				Debug.Assert(clone != null);
				Debug.Assert(clone.Name.StartsWith("riki"));
				Debug.Assert(StaticPoolTest.IsFromPool(clone));
			}
		}

		static Person DoRoundTripTest(SerializerConfig config, string name = "riki")
		{
			var ceras = new CerasSerializer(config);

			var p = new Person();
			p.Name = name;

			var data = ceras.Serialize(p);

			var clone = ceras.Deserialize<Person>(data);
			return clone;
		}

		static class StaticPoolTest
		{
			static HashSet<Person> _objectsCreatedByPool = new HashSet<Person>();

			public static Person CreatePerson()
			{
				var p = new Person();
				_objectsCreatedByPool.Add(p);
				return p;
			}

			public static Person CreatePersonWithName(string name)
			{
				var p = new Person();
				p.Name = name;
				_objectsCreatedByPool.Add(p);
				return p;
			}

			public static bool IsFromPool(Person p) => _objectsCreatedByPool.Contains(p);

			public static void DiscardPooledObjectTest(Person p)
			{
			}
		}

		class InstancePoolTest
		{
			HashSet<Person> _objectsCreatedByPool = new HashSet<Person>();

			public Person CreatePerson()
			{
				var p = new Person();
				_objectsCreatedByPool.Add(p);
				return p;
			}

			public Person CreatePersonWithName(string name)
			{
				var p = new Person();
				p.Name = name;
				_objectsCreatedByPool.Add(p);
				return p;
			}

			public bool IsFromPool(Person p) => _objectsCreatedByPool.Contains(p);

			public void DiscardPooledObjectTest(Person p)
			{
			}
		}



		static void Benchmarks()
		{
			return;

			var config = new CerasGlobalBenchmarkConfig();

			//BenchmarkRunner.Run<MergeBlittingBenchmarks>(config);
			//BenchmarkRunner.Run<Feature_MreRefs_Benchmarks>(config);
			BenchmarkRunner.Run<SerializerComparisonBenchmarks>(config);



			Environment.Exit(0);
		}

		static void TuplesTest()
		{
			var ceras = new CerasSerializer();

			var obj1 = Tuple.Create(5, "a", DateTime.Now, 3.141);

			var data = ceras.Serialize<object>(obj1);
			var clone = ceras.Deserialize<object>(data);

			Debug.Assert(obj1.Equals(clone));



			var obj2 = (234, "bsdasdasdf", DateTime.Now, 34.23424);

			data = ceras.Serialize<object>(obj2);
			clone = ceras.Deserialize<object>(data);

			Debug.Assert(obj2.Equals(clone));
		}

		static void EnsureSealedTypesThrowsException()
		{
			//
			// 1. Check while serializing
			//
			var obj = new List<object>();
			obj.Add(5);
			obj.Add(DateTime.Now);
			obj.Add("asdasdas");
			obj.Add(new Person() { Name = "abc" });

			var config = new SerializerConfig();
			config.KnownTypes.Add(typeof(List<>));
			config.KnownTypes.Add(typeof(int));
			// Some types not added on purpose

			// Should be true by default!
			Debug.Assert(config.Advanced.SealTypesWhenUsingKnownTypes);

			var ceras = new CerasSerializer(config);

			try
			{
				ceras.Serialize(obj);

				Debug.Assert(false, "this line should not be reached, we want an exception here!");
			}
			catch (Exception e)
			{
				// all good
				Console.WriteLine("KnownTypes sealing check successful.");
			}

			//
			// 2. Check while deserializing
			//
			config = new SerializerConfig();
			config.KnownTypes.Add(typeof(List<>));
			config.KnownTypes.Add(typeof(int));
			config.Advanced.SealTypesWhenUsingKnownTypes = false;
			ceras = new CerasSerializer(config);

			var data = ceras.Serialize(obj);

			config = new SerializerConfig();
			config.KnownTypes.Add(typeof(List<>));
			config.KnownTypes.Add(typeof(int));
			config.Advanced.SealTypesWhenUsingKnownTypes = true;
			ceras = new CerasSerializer(config);

			try
			{
				ceras.Deserialize<List<object>>(data);

				Debug.Assert(false, "this line should not be reached, we want an exception here!");
			}
			catch (Exception e)
			{
				// all good
				Console.WriteLine("KnownTypes sealing check successful.");
			}

		}

		static void InjectSpecificFormatterTest()
		{
			var config = new SerializerConfig();
			config.OnResolveFormatter.Add((c, t) =>
			{
				if (t == typeof(Person))
					return new DependencyInjectionTestFormatter();
				return null;
			});

			var ceras = new CerasSerializer(config);

			var f = ceras.GetSpecificFormatter(typeof(Person));

			DependencyInjectionTestFormatter exampleFormatter = (DependencyInjectionTestFormatter)f;

			Debug.Assert(exampleFormatter.Ceras == ceras);
			Debug.Assert(exampleFormatter.EnumFormatter != null);
			Debug.Assert(exampleFormatter == exampleFormatter.Self);

		}

		class DependencyInjectionTestFormatter : IFormatter<Person>
		{
			public CerasSerializer Ceras;
			public EnumFormatter<ByteEnum> EnumFormatter;
			public DependencyInjectionTestFormatter Self;

			public void Serialize(ref byte[] buffer, ref int offset, Person value) => throw new NotImplementedException();
			public void Deserialize(byte[] buffer, ref int offset, ref Person value) => throw new NotImplementedException();
		}

		static void BigIntegerTest()
		{
			BigInteger big = new BigInteger(28364526879);
			big = BigInteger.Pow(big, 6);

			CerasSerializer ceras = new CerasSerializer();

			var data = ceras.Serialize(big);

			var clone = ceras.Deserialize<BigInteger>(data);

			Debug.Assert(clone.ToString() == big.ToString());
		}

		static void ReadonlyTest()
		{
			// Test #1:
			// By default the setting is off. Fields are ignored.
			{
				SerializerConfig config = new SerializerConfig();
				CerasSerializer ceras = new CerasSerializer(config);

				ReadonlyFieldsTest obj = new ReadonlyFieldsTest(5, "xyz", new ReadonlyFieldsTest.ContainerThingA { Setting1 = 10, Setting2 = "asdasdas" });

				var data = ceras.Serialize(obj);

				var cloneNew = ceras.Deserialize<ReadonlyFieldsTest>(data);

				Debug.Assert(cloneNew.Int == 1);
				Debug.Assert(cloneNew.String == "a");
				Debug.Assert(cloneNew.Container == null);
			}

			// Test #2A:
			// In the 'Members' mode we expect an exception for readonly value-typed fields.
			{
				SerializerConfig config = new SerializerConfig();
				// Only use the two "primitives" for this test (string is not a primitive in the original sense tho)
				config.Advanced.ShouldSerializeMember = m => (m.Name == "Int" || m.Name == "String") ? SerializationOverride.ForceInclude : SerializationOverride.ForceSkip;
				config.Advanced.ReadonlyFieldHandling = ReadonlyFieldHandling.Members;

				CerasSerializer ceras = new CerasSerializer(config);

				ReadonlyFieldsTest obj = new ReadonlyFieldsTest(5, "55555", new ReadonlyFieldsTest.ContainerThingA { Setting1 = 555555, Setting2 = "555555555" });

				var data = ceras.Serialize(obj);

				ReadonlyFieldsTest existingTarget = new ReadonlyFieldsTest(6, "66666", null);

				bool gotException = false;
				try
				{
					var cloneNew = ceras.Deserialize<ReadonlyFieldsTest>(data);
				}
				catch (Exception ex)
				{
					gotException = true;
				}

				Debug.Assert(gotException); // We want an exception
			}

			// Test #2B:
			// In the 'Members' mode (when not dealing with value-types)
			// we want Ceras to re-use the already existing object
			{
				SerializerConfig config = new SerializerConfig();
				// We only want the container field, and its contents, but not the two "primitives"
				config.Advanced.ShouldSerializeMember = m => (m.Name == "Int" || m.Name == "String") ? SerializationOverride.ForceSkip : SerializationOverride.ForceInclude;
				config.Advanced.ReadonlyFieldHandling = ReadonlyFieldHandling.Members;

				CerasSerializer ceras = new CerasSerializer(config);

				ReadonlyFieldsTest obj = new ReadonlyFieldsTest(5, "55555", new ReadonlyFieldsTest.ContainerThingA { Setting1 = 555555, Setting2 = "555555555" });

				var data = ceras.Serialize(obj);

				var newContainer = new ReadonlyFieldsTest.ContainerThingA { Setting1 = -1, Setting2 = "this should get overwritten" };
				ReadonlyFieldsTest existingTarget = new ReadonlyFieldsTest(6, "66666", newContainer);

				// populate existing data
				ceras.Deserialize<ReadonlyFieldsTest>(ref existingTarget, data);


				// The simple fields should have been ignored
				Debug.Assert(existingTarget.Int == 6);
				Debug.Assert(existingTarget.String == "66666");

				// The reference itself should not have changed
				Debug.Assert(existingTarget.Container == newContainer);

				// The content of the container should be changed now
				Debug.Assert(newContainer.Setting1 == 555555);
				Debug.Assert(newContainer.Setting2 == "555555555");

			}


			// Test #3
			// In 'ForcedOverwrite' mode Ceras should fix all possible mismatches by force (reflection),
			// which means that it should work exactly like as if the field were not readonly.
			{
				SerializerConfig config = new SerializerConfig();
				config.Advanced.ReadonlyFieldHandling = ReadonlyFieldHandling.ForcedOverwrite;
				CerasSerializer ceras = new CerasSerializer(config);

				// This time we want Ceras to fix everything, reference mismatches and value mismatches alike.

				ReadonlyFieldsTest obj = new ReadonlyFieldsTest(5, "55555", new ReadonlyFieldsTest.ContainerThingA { Setting1 = 324, Setting2 = "1134" });

				var data = ceras.Serialize(obj);

				ReadonlyFieldsTest existingTarget = new ReadonlyFieldsTest(123, null, new ReadonlyFieldsTest.ContainerThingB());

				// populate existing object
				ceras.Deserialize<ReadonlyFieldsTest>(ref existingTarget, data);


				// Now we really check for everything...

				// Sanity check, no way this could happen, but lets make sure.
				Debug.Assert(ReferenceEquals(obj, existingTarget) == false);

				// Fields should be like in the original
				Debug.Assert(existingTarget.Int == 5);
				Debug.Assert(existingTarget.String == "55555");

				// The container type was wrong, Ceras should have fixed that by instantiating a different object 
				// and writing that into the readonly field.
				var container = existingTarget.Container as ReadonlyFieldsTest.ContainerThingA;
				Debug.Assert(container != null);

				// Contents of the container should be correct as well
				Debug.Assert(container.Setting1 == 324);
				Debug.Assert(container.Setting2 == "1134");
			}

			// Test #4:
			// Everything should work fine when using the MemberConfig attribute as well
			{
				var ceras = new CerasSerializer();

				var obj = new ReadonlyFieldsTest2();
				obj.Numbers.Clear();
				obj.Numbers.Add(234);

				var data = ceras.Serialize(obj);

				var clone = new ReadonlyFieldsTest2();
				var originalList = clone.Numbers;
				ceras.Deserialize(ref clone, data);

				Debug.Assert(originalList == clone.Numbers); // actual reference should not have changed
				Debug.Assert(clone.Numbers.Count == 1); // amount of entries should have changed
				Debug.Assert(clone.Numbers[0] == 234); // entry itself should be right
			}

			// todo: also test the case where the existing object does not match the expected type
		}


		class ReadonlyFieldsTest
		{
			public readonly int Int = 1;
			public readonly string String = "a";
			public readonly ContainerBase Container = null;

			public ReadonlyFieldsTest()
			{
			}

			public ReadonlyFieldsTest(int i, string s, ContainerBase c)
			{
				Int = i;
				String = s;
				Container = c;
			}

			public abstract class ContainerBase
			{
			}

			public class ContainerThingA : ContainerBase
			{
				public int Setting1 = 2;
				public string Setting2 = "b";
			}

			public class ContainerThingB : ContainerBase
			{
				public float Float = 1;
				public byte Byte = 1;
				public string String = "c";
			}
		}

		[MemberConfig(ReadonlyFieldHandling = ReadonlyFieldHandling.Members)]
		class ReadonlyFieldsTest2
		{
			public readonly List<int> Numbers = new List<int> { -1, -1, -1, -1 };
		}


		static void MemberInfoAndTypeInfoTest()
		{
			var ceras = new CerasSerializer();

			var multipleTypesHolder = new TypeTestClass();
			multipleTypesHolder.Type1 = typeof(Person);
			multipleTypesHolder.Type2 = typeof(Person);
			multipleTypesHolder.Type3 = typeof(Person);

			multipleTypesHolder.Object1 = new Person();
			multipleTypesHolder.Object2 = new Person();
			multipleTypesHolder.Object3 = multipleTypesHolder.Object1;

			multipleTypesHolder.Member = typeof(TypeTestClass).GetMembers().First();
			multipleTypesHolder.Method = typeof(TypeTestClass).GetMethods().First();


			var data = ceras.Serialize(multipleTypesHolder);
			data.VisualizePrint("member info");
			var multipleTypesHolderClone = ceras.Deserialize<TypeTestClass>(data);

			// todo: check object1 .. 3 as well.

			Debug.Assert(multipleTypesHolder.Member.MetadataToken == multipleTypesHolderClone.Member.MetadataToken);
			Debug.Assert(multipleTypesHolder.Method.MetadataToken == multipleTypesHolderClone.Method.MetadataToken);

			Debug.Assert(multipleTypesHolder.Type1 == multipleTypesHolderClone.Type1);
			Debug.Assert(multipleTypesHolder.Type2 == multipleTypesHolderClone.Type2);
			Debug.Assert(multipleTypesHolder.Type3 == multipleTypesHolderClone.Type3);

		}

		static int Add1(int x) => x + 1;
		static int Add2(int x) => x + 2;

		static void DelegatesTest()
		{
			var config = new SerializerConfig();
			config.Advanced.DelegateSerialization = DelegateSerializationMode.AllowStatic;
			var ceras = new CerasSerializer(config);

			// 1. Simple test: can ceras persist a static-delegate
			{
				Func<int, int> staticFunc = Add1;

				var data = ceras.Serialize(staticFunc);

				var staticFuncClone = ceras.Deserialize<Func<int, int>>(data);

				Debug.Assert(staticFuncClone != null);
				Debug.Assert(object.Equals(staticFunc, staticFuncClone) == true); // must be considered the same
				Debug.Assert(object.ReferenceEquals(staticFunc, staticFuncClone) == false); // must be a new instance


				Debug.Assert(staticFuncClone(5) == staticFunc(5));
			}

			// 2. What about a collection of them
			{
				var rng = new Random();
				List<Func<int, int>> funcs = new List<Func<int, int>>();

				for (int i = 0; i < rng.Next(15, 20); i++)
				{
					Func<int, int> f;

					if (rng.Next(100) < 50)
						f = Add1;
					else
						f = Add2;

					funcs.Add(f);
				}

				var data = ceras.Serialize(funcs);
				var cloneList = ceras.Deserialize<List<Func<int, int>>>(data);

				// Check by checking if the result is the same
				Debug.Assert(funcs.Count == cloneList.Count);
				for (int i = 0; i < funcs.Count; i++)
				{
					var n = rng.Next();
					Debug.Assert(funcs[i](n) == cloneList[i](n));
				}
			}

			// 3. If we switch to "allow instance", it should persist instance-delegates, but no lambdas
			{
				config.Advanced.DelegateSerialization = DelegateSerializationMode.AllowInstance;
				ceras = new CerasSerializer(config);


				//
				// A) Direct Instance
				//
				var str = "abc";
				var substringMethod = typeof(string).GetMethod("Substring", new[] { typeof(int) });
				Func<string, int, string> substring = (Func<string, int, string>)Delegate.CreateDelegate(typeof(Func<string, int, string>), str, substringMethod);

				// Does our delegate even work?
				var testResult = substring(str, 1);
				Debug.Assert(testResult == str.Substring(1));

				// Can we serialize the normal instance delegate?
				var data = ceras.Serialize(substring);
				var substringClone = ceras.Deserialize<Func<string, int, string>>(data);


				//
				// B) Simple lambda
				//

				// Simple lambda with no capture
				// This should probably work, depending on what exactly the compiler generates
				Func<string, int, string> simpleLambda = (s, index) => s.Substring(index);
				var simpleData = ceras.Serialize(simpleLambda);
				var simpleClone = ceras.Deserialize<Func<string, int, string>>(simpleData);

				//
				// C) Lambda with closure
				//

				// Complex lambda with capture
				// capture many random things...
				Func<string, int, string> complexLambda = (s, index) => simpleData.Length + "abc" + index + testResult;
				try
				{
					var complexData = ceras.Serialize(simpleLambda);
					// oh no, this should not be possible
					Debug.Assert(false, "serializing complex lambda should result in an exception!!");
				}
				catch (Exception e)
				{
					// all good!
				}

				//
				// D) Class with event
				//
				// todo: add a warning that "event" fields are compiler generated and thus ignored; or alternatively implement the "why is the schema like it is"-log-feature thing to explain what members where skipped and why.
				config.DefaultTargets = TargetMember.All;
				ceras = new CerasSerializer(config);

				var eventClassType = typeof(DelegateTestClass);

				var eventClass = new DelegateTestClass();
				eventClass.OnSomeNumberHappened += LocalEventHandlerMethod;

				void LocalEventHandlerMethod(int num)
				{
					Console.WriteLine("Event Number: " + num);
				}

				var eventClassData = ceras.Serialize(eventClass);
				var eventClassClone = ceras.Deserialize<DelegateTestClass>(eventClassData);

			}


			return;
			/*
			Func<int, int> myFunc = Add1;

			int localCapturedInt = 6;

			myFunc = x => 
			{
				Console.WriteLine("Original delegate got called!");
				return localCapturedInt + 7;
			};
			
			myFunc = (Func<int, int>)Delegate.Combine(myFunc, myFunc);
			myFunc = (Func<int, int>)Delegate.Combine(myFunc, myFunc);
			myFunc = (Func<int, int>)Delegate.Combine(myFunc, myFunc);

			var targets = myFunc.GetInvocationList();
			

			var result = myFunc(1); // writes the console message above 8 times, *facepalm*

			Debug.Assert(myFunc(5) == 6);

			*/


			// Expected: no type-name appears multiple times, and deserialization works correctly.


			//var multipleTypesHolderData = ceras.Serialize(multipleTypesHolder);
			//multipleTypesHolderData.VisualizePrint("TypeTestClass");
			//var multipleTypesHolderClone = ceras.Deserialize<TypeTestClass>(multipleTypesHolderData);


			/*

			var memberInfo = new MemberInfoHolder();
			memberInfo.Field = typeof(MemberInfoHolder).GetFields()[0];
			memberInfo.Property = typeof(MemberInfoHolder).GetProperty("property", BindingFlags.NonPublic | BindingFlags.Instance);
			memberInfo.Method = typeof(MemberInfoHolder).GetMethod("method", BindingFlags.NonPublic | BindingFlags.Instance);

			var memberInfoClone = ceras.Deserialize<MemberInfoHolder>(ceras.Serialize(memberInfo));

			var valueHolder = new DelegateValueHolder();

			valueHolder.A = 1;
			valueHolder.B = 0;

			Action action = () =>
			{
				valueHolder.B += valueHolder.A;
			};

			HiddenFieldsTestClass test = new HiddenFieldsTestClass();
			test.SimpleActionEvent += () => { };

			var testType = typeof(HiddenFieldsTestClass);


			var clonedAction = ceras.Deserialize<Action>(ceras.Serialize(action));

			clonedAction();

			Func<int> get2 = () => 2;
			var t = get2.GetType();

			var get2Clone = ceras.Deserialize<Func<int>>(ceras.Serialize(get2));


			Debug.Assert(get2() == 2);
			Debug.Assert(get2Clone() == get2());
			*/
		}

		class DelegateTestClass
		{
			public event Action<int> OnSomeNumberHappened;
		}

		class TypeTestClass
		{
			public Type Type1;
			public Type Type2;
			public Type Type3;
			public object Object1;
			public object Object2;
			public object Object3;

			public MemberInfo Member;
			public MethodInfo Method;
		}

		class MemberInfoHolder
		{
			public FieldInfo Field;
			public PropertyInfo Property;
			public MethodInfo Method;

			string property { get; set; }
			void method() { }
		}

		class DelegateValueHolder
		{
			public int A;
			public int B;
		}

		class HiddenFieldsTestClass
		{
			public event Action SimpleActionEvent;
			public event Action<int> SimpleEventWithArg;
		}

		static void SimpleDictionaryTest()
		{
			var dict = new Dictionary<string, object>
			{
				["name"] = "Test",
			};
			var s = new CerasSerializer();

			var data = s.Serialize(dict);
			var clone = s.Deserialize<Dictionary<string, object>>(data);

			Debug.Assert(dict != clone);

			string n1 = dict["name"] as string;
			string n2 = clone["name"] as string;
			Debug.Assert(n1 == n2);
		}

		static void DictInObjArrayTest()
		{
			var dict = new Dictionary<string, object>
			{
				["test"] = new Dictionary<string, object>
				{
					["test"] = new object[]
					{
						new Dictionary<string, object>
						{
							["test"] = 3
						}
					}
				}
			};


			var s = new CerasSerializer();

			var data = s.Serialize(dict);

			var cloneDict = s.Deserialize<Dictionary<string, object>>(data);

			var inner1 = cloneDict["test"] as Dictionary<string, object>;
			Debug.Assert(inner1 != null);

			var objArray = inner1["test"] as object[];
			Debug.Assert(objArray != null);

			var dictElement = objArray[0] as Dictionary<string, object>;
			Debug.Assert(dictElement != null);

			var three = dictElement["test"];

			Debug.Assert(three.GetType() == typeof(int));
			Debug.Assert(3.Equals(three));
		}

		static void MaintainTypeTest()
		{
			CerasSerializer ceras = new CerasSerializer();

			var dict = new Dictionary<string, object>
			{
				["int"] = 5,
				["byte"] = (byte)12,
				["float"] = 3.141f,
				["ushort"] = (ushort)345,
				["sbyte"] = (sbyte)91,
			};

			var data1 = ceras.Serialize(dict);
			var clone = ceras.Deserialize<Dictionary<string, object>>(data1);

			foreach (var kvp in dict)
			{
				var cloneValue = clone[kvp.Key];

				Debug.Assert(kvp.Value.Equals(cloneValue));

				if (kvp.Value.GetType() != cloneValue.GetType())
					Debug.Assert(false, $"Type does not match: A={kvp.Value.GetType()} B={cloneValue.GetType()}");
				else
					Console.WriteLine($"Success! Type matching: {kvp.Value.GetType()}");
			}

			var data2 = new Dictionary<string, object>();
			data2["test"] = 5;

			var s = new CerasSerializer();
			var clonedDict = s.Deserialize<Dictionary<string, object>>(s.Serialize(data2));

			var originalType = data2["test"].GetType();
			var clonedType = clonedDict["test"].GetType();

			if (originalType != clonedType)
			{
				Debug.Assert(false, $"Types don't match anymore!! {originalType} {clonedType}");
			}
			else
			{
				Console.WriteLine("Success! Type match: " + originalType);
			}

		}

		static void InterfaceFormatterTest()
		{
			CerasSerializer ceras = new CerasSerializer();

			var intListFormatter = ceras.GetFormatter<IList<int>>();

			List<int> list = new List<int> { 1, 2, 3, 4 };


			byte[] buffer = new byte[200];
			int offset = 0;
			intListFormatter.Serialize(ref buffer, ref offset, list);


			// Deserializing into a IList variable should be no problem!

			offset = 0;
			IList<int> clonedList = null;
			intListFormatter.Deserialize(buffer, ref offset, ref clonedList);

			Debug.Assert(clonedList != null);
			Debug.Assert(clonedList.SequenceEqual(list));
		}

		public abstract class NetObjectMessage
		{
			public uint NetId;
		}
		public class SyncUnitHealth : NetObjectMessage
		{
			public System.Int32 Health;
		}

		static void InheritTest()
		{
			var config = new SerializerConfig();
			config.KnownTypes.Add(typeof(SyncUnitHealth));
			var ceras = new CerasSerializer(config);

			// This should be no problem:
			// - including inherited fields
			// - registering as derived (when derived is used), but still including inherited fields
			// There's literally no reason why this shouldn't work (except for some major bug ofc)

			var obj = new SyncUnitHealth { NetId = 1235, Health = 600 };
			var bytes = ceras.Serialize<object>(obj);

			var clone = ceras.Deserialize<object>(bytes) as SyncUnitHealth;

			Debug.Assert(obj != clone);
			Debug.Assert(obj.NetId == clone.NetId);
			Debug.Assert(obj.Health == clone.Health);

			// we're using KnownTypes, so we expect the message to be really short
			Debug.Assert(bytes.Length == 6);
		}

		class StructTestClass
		{
			public TestStruct TestStruct;
		}

		public struct TestStruct
		{
			[Ceras.Include]
			uint _value;

			public static implicit operator uint(TestStruct id)
			{
				return id._value;
			}
			public static implicit operator TestStruct(uint id)
			{
				return new TestStruct { _value = id };
			}

			public override string ToString()
			{
				return _value.ToString("X");
			}
		}

		static void StructTest()
		{
			var c = new StructTestClass();
			c.TestStruct = 5;

			var ceras = new CerasSerializer();
			var data = ceras.Serialize<object>(c);
			var clone = ceras.Deserialize<object>(data);

			data.VisualizePrint("Struct Test");

			var cloneContainer = clone as StructTestClass;

			Debug.Assert(c.TestStruct == cloneContainer.TestStruct);
		}

		static void VersionToleranceTest()
		{
			var config = new SerializerConfig();
			config.VersionTolerance = VersionTolerance.AutomaticEmbedded;

			config.Advanced.TypeBinder = new DebugVersionTypeBinder();

			// We are using a new ceras instance every time.
			// We want to make sure that no caching is going on.
			// todo: we have to run the same tests with only one instance to test the opposite, which is that cached stuff won't get in the way!

			var v1 = new VersionTest1 { A = 33, B = 34, C = 36 };
			var v2 = new VersionTest2 { A = -3, C2 = -6, D = -7 };

			var v1Data = (new CerasSerializer(config)).Serialize(v1);
			v1Data.VisualizePrint("data with version tolerance");
			(new CerasSerializer(config)).Deserialize<VersionTest2>(ref v2, v1Data);

			Debug.Assert(v1.A == v2.A, "normal prop did not persist");
			Debug.Assert(v1.C == v2.C2, "expected prop 'C2' to be populated by prop previously named 'C'");


			// Everything should work the same way when forcing serialization to <object>
			var v1DataAsObj = (new CerasSerializer(config)).Serialize<object>(v1);
			v1DataAsObj.VisualizePrint("data with version tolerance (as object)");
			var v1Clone = (new CerasSerializer(config)).Deserialize<object>(v1DataAsObj);

			var v1CloneCasted = v1Clone as VersionTest2;
			Debug.Assert(v1CloneCasted != null, "expected deserialized object to have changed to the newer type");
			Debug.Assert(v1CloneCasted.A == v1.A, "expected A to stay the same");
			Debug.Assert(v1CloneCasted.C2 == v1.C, "expected C to be transferred to C2");
			Debug.Assert(v1CloneCasted.D == new VersionTest2().D, "expected D to have the default value");


			// todo: we have to add a test for the case when we read some old data, and the root object has not changed (so it's still the same as always), but a child object has changed
			// todo: test the case where a user-value-type is a field in some root object, and while reading the schema changes (because are reading old data), an exception is expected/wanted
			// todo: test reading multiple different old serializations in random order; each one encoding a different version of the object; 
		}

		static void WrongRefTypeTest()
		{
			var ceras = new CerasSerializer();

			var container = new WrongRefTypeTestClass();

			LinkedList<int> list = new LinkedList<int>();
			list.AddLast(6);
			list.AddLast(2);
			list.AddLast(7);
			container.Collection = list;

			var data = ceras.Serialize(container);
			var linkedListClone = ceras.Deserialize<WrongRefTypeTestClass>(data);
			var listClone = linkedListClone.Collection as LinkedList<int>;

			Debug.Assert(listClone != null);
			Debug.Assert(listClone.Count == 3);
			Debug.Assert(listClone.First.Value == 6);

			// Now the actual test:
			// We change the type that is actually inside
			// And next ask to deserialize into the changed instance!
			// What we expect to happen is that ceras sees that the type is wrong and creates a new object
			container.Collection = new List<int>();

			ceras.Deserialize(ref container, data);

			Debug.Assert(container.Collection is LinkedList<int>);
		}

		class WrongRefTypeTestClass
		{
			public ICollection<int> Collection;
		}

		static void PerfTest()
		{
			// todo: compare against msgpack

			// 1.) Primitives
			// Compare encoding of a mix of small and large numbers to test var-int encoding speed
			var rng = new Random();

			List<int> numbers = new List<int>();
			for (int i = 0; i < 200; i++)
				numbers.Add(i);
			for (int i = 1000; i < 1200; i++)
				numbers.Add(i);
			for (int i = short.MaxValue + 1000; i < short.MaxValue + 1200; i++)
				numbers.Add(i);
			numbers = numbers.OrderBy(n => rng.Next(1000)).ToList();

			var ceras = new CerasSerializer();

			var cerasData = ceras.Serialize(numbers);



			// 2.) Object Data
			// Many fields/properties, some nesting



			/*
			 * todo
			 *
			 * - prewarm proxy pool; prewarm 
			 *
			 * - would ThreadsafeTypeKeyHashTable actually help for the cases where we need to type switch?
			 *
			 * - reference lookups take some time; we could disable them by default and instead let the user manually enable reference serialization per type
			 *      config.EnableReference(typeof(MyObj));
			 *
			 * - directly inline all primitive reader/writer functions. Instead of creating an Int32Formatter the dynamic formatter directly calls the matching method
			 *
			 * - potentially improve number encoding speed (varint encoding is naturally not super fast, maybe we can apply some tricks...)
			 *
			 * - have DynamicObjectFormatter generate its expressions, but inline the result directly to the reference formatter
			 *
			 * - reference proxies: use array instead of a list, don't return references to a pool, just reset them!
			 *
			 * - when we're later dealing with version tolerance, we write all the the type definitions first, and have a skip offset in front of each object
			 *
			 * - avoid overhead of "Formatter" classes for all primitives and directly use them, they can also be accessed through a static generic
			 *
			 * - would a specialized formatter for List<> help? maybe, we'd avoid interfaces vtable calls
			 *
			 * - use static generic caching where possible (rarely the case since ceras can be instantiated multiple times with different settings)
			 *
			 * - primitive arrays can be cast and blitted directly
			 *
			 * - optimize simple properties: serializing the backing field directly, don't call Get/Set (add a setting so it can be deactivated)
			*/
		}

		static void TupleTest()
		{
			// todo:
			//
			// - ValueTuple: can already be serialized as is! We just need to somehow enforce serialization of public fields
			//	 maybe a predefined list of fixed overrides? An additional step directly after ShouldSerializeMember?
			//
			// - Tuple: does not work and (for now) can't be fixed. 
			//   we'll need support for a different kind of ReferenceSerializer (one that does not create an instance)
			//   and a different DynamicSerializer (one that collects the values into local variables, then instantiates the object)
			//

			SerializerConfig config = new SerializerConfig();
			config.DefaultTargets = TargetMember.AllPublic;
			var ceras = new CerasSerializer(config);

			var vt = ValueTuple.Create(5, "b", DateTime.Now);

			var data = ceras.Serialize(vt);
			var vtClone = ceras.Deserialize<ValueTuple<int, string, DateTime>>(data);

			Debug.Assert(vt.Item1 == vtClone.Item1);
			Debug.Assert(vt.Item2 == vtClone.Item2);
			Debug.Assert(vt.Item3 == vtClone.Item3);

			//var t = Tuple.Create(5, "b", DateTime.Now);
			//data = ceras.Serialize(vt);
			//var tClone = ceras.Deserialize<Tuple<int, string, DateTime>>(data);
		}

		static void NullableTest()
		{
			var ceras = new CerasSerializer();

			var obj = new NullableTestClass
			{
				A = 12.00000476M,
				B = 13.000001326M,
				C = 14,
				D = 15
			};

			var data = ceras.Serialize(obj);
			var clone = ceras.Deserialize<NullableTestClass>(data);

			Debug.Assert(obj.A == clone.A);
			Debug.Assert(obj.B == clone.B);
			Debug.Assert(obj.C == clone.C);
			Debug.Assert(obj.D == clone.D);
		}

		class NullableTestClass
		{
			public decimal A;
			public decimal? B;
			public byte C;
			public byte? D;
		}

		static void ErrorOnDirectEnumerable()
		{
			// Enumerables obviously cannot be serialized
			// Would we resolve it into a list? Or serialize the "description" / linq-projection it represents??
			// What if its a network-stream? Its just not feasible.

			var ar = new[] { 1, 2, 3, 4 };
			IEnumerable<int> enumerable = ar.Select(x => x + 1);

			try
			{
				var ceras = new CerasSerializer();
				var data = ceras.Serialize(enumerable);

				Debug.Assert(false, "Serialization of IEnumerator is supposed to fail, but it did not!");
			}
			catch (Exception e)
			{
				// All good, we WANT an exception
			}


			var container = new GenericTest<IEnumerable<int>> { Value = enumerable };
			try
			{
				var ceras = new CerasSerializer();
				var data = ceras.Serialize(container);

				Debug.Assert(false, "Serialization of IEnumerator is supposed to fail, but it did not!");
			}
			catch (Exception e)
			{
				// All good, we WANT an exception
			}
		}

		static void CtorTest()
		{
			var obj = new ConstructorTest(5);
			var ceras = new CerasSerializer();

			// This is expected to throw an exception
			try
			{
				var data = ceras.Serialize(obj);
				var clone = ceras.Deserialize<ConstructorTest>(data);

				Debug.Assert(false, "deserialization was supposed to fail, but it didn't!");
			}
			catch (Exception e)
			{
				// This is ok and expected!
				// The object does not have a parameterless constructor on purpose.

				// Support for that is already on the todo list.
			}
		}

		static void PropertyTest()
		{
			var p = new PropertyClass()
			{
				Name = "qweqrwetwr",
				Num = 348765213,
				Other = new OtherPropertyClass()
			};
			p.MutateProperties();
			p.Other.Other = p;
			p.Other.PropertyClasses.Add(p);
			p.Other.PropertyClasses.Add(p);

			var config = new SerializerConfig();
			config.DefaultTargets = TargetMember.All;

			var ceras = new CerasSerializer(config);
			var data = ceras.Serialize(p);
			data.VisualizePrint("Property Test");
			var clone = ceras.Deserialize<PropertyClass>(data);

			Debug.Assert(p.Name == clone.Name);
			Debug.Assert(p.Num == clone.Num);
			Debug.Assert(p.Other.PropertyClasses.Count == 2);
			Debug.Assert(p.Other.PropertyClasses[0] == p.Other.PropertyClasses[1]);

			Debug.Assert(p.VerifyAllPropsAreChanged());

		}

		static void ListTest()
		{
			var data = new List<int> { 6, 32, 573, 246, 24, 2, 9 };

			var s = new CerasSerializer();

			var p = new Person() { Name = "abc", Health = 30 };
			var pData = s.Serialize<object>(p);
			pData.VisualizePrint("person data");
			var pClone = (Person)s.Deserialize<object>(pData);
			Assert.Equal(p.Health, pClone.Health);
			Assert.Equal(p.Name, pClone.Name);


			var serialized = s.Serialize(data);
			var clone = s.Deserialize<List<int>>(serialized);
			Assert.Equal(data.Count, clone.Count);
			for (int i = 0; i < data.Count; i++)
				Assert.Equal(data[i], clone[i]);


			var serializedAsObject = s.Serialize<object>(data);
			var cloneObject = s.Deserialize<object>(serializedAsObject);

			Assert.Equal(data.Count, ((List<int>)cloneObject).Count);

			for (int i = 0; i < data.Count; i++)
				Assert.Equal(data[i], ((List<int>)cloneObject)[i]);
		}

		static void ComplexTest()
		{
			var s = new CerasSerializer();

			var c = new ComplexClass();
			var complexClassData = s.Serialize(c);
			complexClassData.VisualizePrint("Complex Data");

			var clone = s.Deserialize<ComplexClass>(complexClassData);

			Debug.Assert(!ReferenceEquals(clone, c));
			Debug.Assert(c.Num == clone.Num);
			Debug.Assert(c.SetName.Name == clone.SetName.Name);
			Debug.Assert(c.SetName.Type == clone.SetName.Type);
		}

		static void EnumTest()
		{
			var s = new CerasSerializer();

			var longEnum = LongEnum.b;

			var longEnumData = s.Serialize(longEnum);
			var cloneLong = s.Deserialize<LongEnum>(longEnumData);
			Debug.Assert(cloneLong == longEnum);


			var byteEnum = ByteEnum.b;
			var cloneByte = s.Deserialize<ByteEnum>(s.Serialize(byteEnum));
			Debug.Assert(byteEnum == cloneByte);
		}

		static void GuidTest()
		{
			var s = new CerasSerializer();

			var g = staticGuid;

			// As real type (generic call)
			var guidData = s.Serialize(g);
			Debug.Assert(guidData.Length == 16);

			var guidClone = s.Deserialize<Guid>(guidData);
			Debug.Assert(g == guidClone);

			// As Object
			var guidAsObjData = s.Serialize<object>(g);
			Debug.Assert(guidAsObjData.Length > 16); // now includes type-data, so it has to be larger
			var objClone = s.Deserialize<object>(guidAsObjData);
			var objCloneCasted = (Guid)objClone;

			Debug.Assert(objCloneCasted == g);

		}

		static void NetworkTest()
		{
			var config = new SerializerConfig();
			config.Advanced.PersistTypeCache = true;
			config.KnownTypes.Add(typeof(SetName));
			config.KnownTypes.Add(typeof(NewPlayer));
			config.KnownTypes.Add(typeof(LongEnum));
			config.KnownTypes.Add(typeof(ByteEnum));
			config.KnownTypes.Add(typeof(ComplexClass));
			config.KnownTypes.Add(typeof(Complex2));

			var msg = new SetName
			{
				Name = "abc",
				Type = SetName.SetNameType.Join
			};

			CerasSerializer sender = new CerasSerializer(config);
			CerasSerializer receiver = new CerasSerializer(config);

			Console.WriteLine("Hash: " + sender.ProtocolChecksum.Checksum);

			var data = sender.Serialize<object>(msg);
			PrintData(data);
			data = sender.Serialize<object>(msg);
			PrintData(data);

			var obj = receiver.Deserialize<object>(data);
			var clone = (SetName)obj;

			Debug.Assert(clone.Name == msg.Name);
			Debug.Assert(clone.Type == msg.Type);
		}

		static void PrintData(byte[] data)
		{
			var text = BitConverter.ToString(data);
			Console.WriteLine(data.Length + " bytes: " + text);
		}
	}

	class DebugVersionTypeBinder : ITypeBinder
	{
		Dictionary<Type, string> _commonNames = new Dictionary<Type, string>
		{
				{ typeof(VersionTest1), "*" },
				{ typeof(VersionTest2), "*" }
		};

		public string GetBaseName(Type type)
		{
			if (_commonNames.TryGetValue(type, out string v))
				return v;

			return SimpleTypeBinderHelper.GetBaseName(type);
		}

		public Type GetTypeFromBase(string baseTypeName)
		{
			// While reading, we want to resolve to 'VersionTest2'
			// So we can simulate that the type changed.
			if (_commonNames.ContainsValue(baseTypeName))
				return typeof(VersionTest2);

			return SimpleTypeBinderHelper.GetTypeFromBase(baseTypeName);
		}

		public Type GetTypeFromBaseAndAgruments(string baseTypeName, params Type[] genericTypeArguments)
		{
			throw new NotSupportedException("this binder is only for debugging");
			// return SimpleTypeBinderHelper.GetTypeFromBaseAndAgruments(baseTypeName, genericTypeArguments);
		}
	}

	class VersionTest1
	{
		public int A = -11;
		public int B = -12;
		public int C = -13;
	}
	class VersionTest2
	{
		// A stays as it is
		public int A = 50;

		// B got removed
		// --

		[PreviousName("C", "C2")]
		public int C2 = 52;

		// D is new
		public int D = 53;
	}

	class ConstructorTest
	{
		public int x;

		public ConstructorTest(int x)
		{
			this.x = x;
		}
	}

	public enum LongEnum : long
	{
		a = 1,
		b = long.MaxValue - 500
	}

	public enum ByteEnum : byte
	{
		a = 1,
		b = 200,
	}

	class SetName
	{
		public SetNameType Type;
		public string Name;

		public enum SetNameType
		{
			Initial, Change, Join
		}

		public SetName()
		{

		}
	}

	class NewPlayer
	{
		public string Guid;
	}

	interface IComplexInterface { }
	interface IComplexA : IComplexInterface { }
	interface IComplexB : IComplexInterface { }
	interface IComplexX : IComplexA, IComplexB { }

	class Complex2 : IComplexX
	{
		public IComplexB Self;
		public ComplexClass Parent;
	}

	class ComplexClass : IComplexA
	{
		static Random rng = new Random(9);

		public int Num;
		public IComplexA RefA;
		public IComplexB RefB;
		public SetName SetName;

		public ComplexClass()
		{
			Num = rng.Next(0, 10);
			if (Num < 8)
			{
				RefA = new ComplexClass();

				var c2 = new Complex2 { Parent = this };
				c2.Self = c2;
				RefB = c2;

				SetName = new SetName { Type = SetName.SetNameType.Change, Name = "asd" };
			}
		}
	}

	class PropertyClass
	{
		public string Name { get; set; } = "abcdef";
		public int Num { get; set; } = 6235;
		public OtherPropertyClass Other { get; set; }

		public string PublicProp { get; set; } = "Public Prop (default value)";
		internal string InternalProp { get; set; } = "Internal Prop (default value)";
		string PrivateProp { get; set; } = "Private Prop (default value)";
		protected string ProtectedProp { get; set; } = "Protected Prop (default value)";
		public string ReadonlyProp1 { get; private set; } = "ReadOnly Prop (default value)";

		public PropertyClass()
		{
		}

		internal void MutateProperties()
		{
			PublicProp = "changed";
			InternalProp = "changed";
			PrivateProp = "changed";
			ProtectedProp = "changed";
			ReadonlyProp1 = "changed";
		}

		internal bool VerifyAllPropsAreChanged()
		{
			return PublicProp == "changed"
				&& InternalProp == "changed"
				&& PrivateProp == "changed"
				&& ProtectedProp == "changed"
				&& ReadonlyProp1 == "changed";
		}
	}

	[MemberConfig(TargetMembers = TargetMember.All)]
	class OtherPropertyClass
	{
		public PropertyClass Other { get; set; }
		public List<PropertyClass> PropertyClasses { get; set; } = new List<PropertyClass>();
	}

	class GenericTest<T>
	{
		public T Value;
	}
}
