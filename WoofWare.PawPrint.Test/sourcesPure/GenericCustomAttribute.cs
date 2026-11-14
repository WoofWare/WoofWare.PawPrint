using System;
using System.Collections.Generic;
using System.Reflection;

// C# 11's generic attributes. The constructor of `[One<int>(...)]` is a MemberRef whose parent is
// a TypeSpec (`OneAttribute<int32>`), so the attribute's type comes from that TypeSpec and its
// constructor's parameter types are the definition's, read under the TypeSpec's instantiation.
// CoreLib takes a different route to such a constructor than to a non-generic one
// (RuntimeCustomAttributeData.cs `FilterCustomAttributeRecord`): `Module.ResolveMethod` with the
// attribute type's arguments, then `.MethodHandle.GetMethodInfo()`, which hands the
// `CustomAttribute_CreateCustomAttributeInstance` QCall the `RuntimeConstructorInfo` itself
// rather than a `RuntimeMethodInfoStub`.
//
// The cases cover one and two type arguments, a value-type argument, a type argument that is
// itself a generic instantiation (of a CoreLib type and of this assembly's own), and constructor
// and named arguments (property and field) typed by the type parameter, on a type, a method, a
// field and a parameter. Every constructor carries a trailing int, so a fixed argument decoded at
// the wrong width desynchronises the cursor and corrupts the tail.

[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class OneAttribute<T> : Attribute
{
    public OneAttribute(T value, int tail)
    {
        Value = value;
        Tail = tail;
    }

    public T Value { get; }
    public int Tail { get; }

    // Named argument through the property route, typed by the type parameter.
    public T Extra { get; set; }

    // Named argument through the field route, typed by the type parameter.
    public T ExtraField;
}

[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class TwoAttribute<TKey, TValue> : Attribute
{
    public TwoAttribute(TValue value, int tail)
    {
        Value = value;
        Tail = tail;
    }

    public TValue Value { get; }
    public int Tail { get; }
    public TKey Key { get; set; }
}

// Its type argument appears only in its type, never in an argument.
[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class TagAttribute<T> : Attribute
{
    public TagAttribute(int tail)
    {
        Tail = tail;
    }

    public int Tail { get; }

    public Type Argument => typeof(T);
}

public sealed class Box<T>
{
}

public struct Point
{
    public int X;
}

[One<int>(5, 10, Extra = 9, ExtraField = -3)]
[One<string>("s", 11, Extra = "named")]
[Two<string, int>(7, 12, Key = "k")]
[Two<Box<int>, string>("v", 13)]
[Tag<List<int>>(14)]
[Tag<Box<string>>(15)]
[Tag<Point>(16)]
public sealed class Decorated
{
    [One<string>("f", 20, ExtraField = "ef")]
    [Tag<Box<Point>>(21)]
    public static int Field;

    [Two<int, string>("m", 30, Key = 4)]
    [One<long>(1L << 40, 31)]
    public static void Method([One<int>(-1, 40)] [Tag<Box<int>>(41)] int parameter)
    {
    }
}

public class GenericCustomAttribute
{
    static int next = 1;
    static int firstFailure = 0;

    static void Check(bool ok)
    {
        int index = next;
        next = next + 1;
        if (!ok && firstFailure == 0)
        {
            firstFailure = index;
        }
    }

    // Attribute order within one target is not something this test should pin, so each
    // application is picked out by the tail only it carries.
    static T ByTail<T>(object[] attrs, Func<T, int> tail, int wanted) where T : class
    {
        T found = null;
        int matches = 0;
        foreach (object a in attrs)
        {
            if (a is T t && tail(t) == wanted)
            {
                found = t;
                matches++;
            }
        }

        Check(matches == 1);
        return found;
    }

    static CustomAttributeData DataOf(IList<CustomAttributeData> data, Type attributeType)
    {
        CustomAttributeData found = null;
        int matches = 0;
        foreach (CustomAttributeData d in data)
        {
            if (d.AttributeType == attributeType)
            {
                found = d;
                matches++;
            }
        }

        Check(matches == 1);
        return found;
    }

    static object NamedValue(CustomAttributeData data, string name)
    {
        foreach (CustomAttributeNamedArgument n in data.NamedArguments)
        {
            if (n.MemberName == name)
            {
                return n.TypedValue.Value;
            }
        }

        return null;
    }

    static void TypeTarget()
    {
        Type t = typeof(Decorated);

        // Filtering by a closed generic attribute type selects that instantiation alone.
        object[] oneInt = t.GetCustomAttributes(typeof(OneAttribute<int>), false);
        Check(oneInt.Length == 1);
        OneAttribute<int> a = ByTail<OneAttribute<int>>(oneInt, x => x.Tail, 10);
        Check(a != null && a.Value == 5 && a.Extra == 9 && a.ExtraField == -3);

        object[] oneString = t.GetCustomAttributes(typeof(OneAttribute<string>), false);
        Check(oneString.Length == 1);
        OneAttribute<string> s = ByTail<OneAttribute<string>>(oneString, x => x.Tail, 11);
        Check(s != null && s.Value == "s" && s.Extra == "named" && s.ExtraField == null);

        Check(t.GetCustomAttributes(typeof(OneAttribute<long>), false).Length == 0);

        // An open generic filter selects every instantiation of its definition
        // (RuntimeCustomAttributeData.cs `MatchesTypeFilter`), and the result is an `Attribute[]`.
        object[] oneOpen = t.GetCustomAttributes(typeof(OneAttribute<>), false);
        Check(oneOpen.Length == 2);
        Check(oneOpen is Attribute[]);
        Check(ByTail<OneAttribute<int>>(oneOpen, x => x.Tail, 10) != null);
        Check(ByTail<OneAttribute<string>>(oneOpen, x => x.Tail, 11) != null);
        Check(t.GetCustomAttributes(typeof(TagAttribute<>), false).Length == 3);

        // Every application at once.
        object[] all = t.GetCustomAttributes(false);
        Check(all.Length == 7);

        TwoAttribute<string, int> two = ByTail<TwoAttribute<string, int>>(all, x => x.Tail, 12);
        Check(two != null && two.Value == 7 && two.Key == "k");

        TwoAttribute<Box<int>, string> twoBox = ByTail<TwoAttribute<Box<int>, string>>(all, x => x.Tail, 13);
        Check(twoBox != null && twoBox.Value == "v" && twoBox.Key == null);

        TagAttribute<List<int>> tagList = ByTail<TagAttribute<List<int>>>(all, x => x.Tail, 14);
        Check(tagList != null && tagList.Argument == typeof(List<int>));

        TagAttribute<Box<string>> tagBox = ByTail<TagAttribute<Box<string>>>(all, x => x.Tail, 15);
        Check(tagBox != null && tagBox.Argument == typeof(Box<string>));

        TagAttribute<Point> tagPoint = ByTail<TagAttribute<Point>>(all, x => x.Tail, 16);
        Check(tagPoint != null && tagPoint.Argument == typeof(Point));

        // The generic extension method, which casts to its type argument.
        OneAttribute<int> viaGeneric = t.GetCustomAttribute<OneAttribute<int>>();
        Check(viaGeneric != null && viaGeneric.Tail == 10);
        Check(t.GetCustomAttribute<TagAttribute<Box<string>>>().Tail == 15);
        Check(t.GetCustomAttribute<TagAttribute<Box<int>>>() == null);

        Check(t.IsDefined(typeof(OneAttribute<int>), false));
        Check(t.IsDefined(typeof(TwoAttribute<Box<int>, string>), false));
        Check(!t.IsDefined(typeof(TwoAttribute<int, string>), false));
        Check(t.IsDefined(typeof(TwoAttribute<,>), false));
        Check(Attribute.IsDefined(t, typeof(TagAttribute<Point>)));

        IList<CustomAttributeData> data = t.GetCustomAttributesData();
        Check(data.Count == 7);

        CustomAttributeData oneData = DataOf(data, typeof(OneAttribute<int>));
        Check(oneData != null);
        if (oneData != null)
        {
            Check(oneData.Constructor.DeclaringType == typeof(OneAttribute<int>));
            Check(oneData.ConstructorArguments.Count == 2);
            Check(oneData.ConstructorArguments[0].ArgumentType == typeof(int));
            Check((int)oneData.ConstructorArguments[0].Value == 5);
            Check((int)oneData.ConstructorArguments[1].Value == 10);
            Check(oneData.NamedArguments.Count == 2);
            Check((int)NamedValue(oneData, "Extra") == 9);
            Check((int)NamedValue(oneData, "ExtraField") == -3);
        }

        CustomAttributeData twoData = DataOf(data, typeof(TwoAttribute<string, int>));
        Check(twoData != null);
        if (twoData != null)
        {
            Check(twoData.ConstructorArguments[0].ArgumentType == typeof(int));
            Check((int)twoData.ConstructorArguments[0].Value == 7);
            Check((string)NamedValue(twoData, "Key") == "k");
        }

        CustomAttributeData tagData = DataOf(data, typeof(TagAttribute<Box<string>>));
        Check(tagData != null);
        if (tagData != null)
        {
            Check(tagData.Constructor.DeclaringType == typeof(TagAttribute<Box<string>>));
            Check((int)tagData.ConstructorArguments[0].Value == 15);
        }
    }

    // Only filtered reads here: an unfiltered read of a field's attributes (`GetCustomAttributes(false)`,
    // `GetCustomAttributesData()`) also asks for its pseudo-attributes, which asks
    // `MetadataImport.GetFieldOffset`, whatever the attributes are. `FieldCustomAttributesUnfiltered.cs`
    // holds that path.
    static void FieldTarget()
    {
        FieldInfo f = typeof(Decorated).GetField("Field");

        object[] ones = f.GetCustomAttributes(typeof(OneAttribute<string>), false);
        Check(ones.Length == 1);
        OneAttribute<string> one = ByTail<OneAttribute<string>>(ones, x => x.Tail, 20);
        Check(one != null && one.Value == "f" && one.ExtraField == "ef" && one.Extra == null);

        Check(f.GetCustomAttributes(typeof(TagAttribute<>), false).Length == 1);
        Check(f.GetCustomAttribute<TagAttribute<Box<Point>>>().Tail == 21);
        Check(f.IsDefined(typeof(OneAttribute<string>), false));
        Check(!f.IsDefined(typeof(OneAttribute<int>), false));
        Check(!f.IsDefined(typeof(TwoAttribute<,>), false));
    }

    static void MethodTarget()
    {
        MethodInfo m = typeof(Decorated).GetMethod("Method");

        object[] all = m.GetCustomAttributes(false);
        Check(all.Length == 2);

        TwoAttribute<int, string> two = ByTail<TwoAttribute<int, string>>(all, x => x.Tail, 30);
        Check(two != null && two.Value == "m" && two.Key == 4);

        OneAttribute<long> one = m.GetCustomAttribute<OneAttribute<long>>();
        Check(one != null && one.Value == (1L << 40) && one.Tail == 31);

        Check(m.IsDefined(typeof(TwoAttribute<int, string>), false));
        Check(Attribute.GetCustomAttributes(m, typeof(OneAttribute<>)).Length == 1);

        CustomAttributeData data = DataOf(m.GetCustomAttributesData(), typeof(OneAttribute<long>));
        Check(data != null);
        if (data != null)
        {
            Check(data.ConstructorArguments[0].ArgumentType == typeof(long));
            Check((long)data.ConstructorArguments[0].Value == (1L << 40));
        }
    }

    static void ParameterTarget()
    {
        ParameterInfo p = typeof(Decorated).GetMethod("Method").GetParameters()[0];

        object[] all = p.GetCustomAttributes(false);
        Check(all.Length == 2);

        OneAttribute<int> one = ByTail<OneAttribute<int>>(all, x => x.Tail, 40);
        Check(one != null && one.Value == -1);

        Check(p.GetCustomAttribute<TagAttribute<Box<int>>>().Tail == 41);
        Check(p.IsDefined(typeof(OneAttribute<int>), false));
        Check(!p.IsDefined(typeof(OneAttribute<string>), false));

        CustomAttributeData data = DataOf(p.GetCustomAttributesData(), typeof(TagAttribute<Box<int>>));
        Check(data != null && (int)data.ConstructorArguments[0].Value == 41);
    }

    public static int Main(string[] argv)
    {
        TypeTarget();
        FieldTarget();
        MethodTarget();
        ParameterTarget();
        return firstFailure;
    }
}
