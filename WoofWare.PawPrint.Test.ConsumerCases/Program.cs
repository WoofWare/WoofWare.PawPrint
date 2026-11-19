// Scenarios from the mocking libraries of issue #2092, one per argument, each run by
// TestConsumerCases under PawPrint and under real .NET, which compares the two runs' endings and
// standard output.
//
// Exit codes: 0 when the scenario ran as expected; 1 when a value it computed was wrong, with what
// differed on standard output; 2 for an argument that names no scenario; 3 when a scenario threw,
// with the exception on standard output. The argument `list` prints every scenario's name, one per
// line, in the order the fixture states them.
//
// Every line a scenario prints must be the same in every process: real .NET randomises string
// hashing per process, so nothing here prints in the order of a hashed collection.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FakeItEasy;

public interface IThing
{
    int Value { get; }
    string Name { get; set; }
    string Describe(int x, string y);
    void Act(int x);
    bool TryGet(string key, out int value);
    T Echo<T>(T x);
    Task<int> ComputeAsync(int x);
    event EventHandler Changed;
}

public interface IRepo<T>
{
    T Get(int id);
    void Put(int id, T value);
}

public abstract class AbstractThing
{
    protected AbstractThing(int seed) { Seed = seed; }
    public int Seed { get; }
    public abstract int Compute(int x);
    public virtual int Twice(int x) { return 2 * Compute(x); }
}

public class Concrete
{
    public virtual int Add(int a, int b) { return a + b; }
    public virtual string Name { get; set; }
    public virtual T Id<T>(T x) { return x; }
}

public class CountingInterceptor : Castle.DynamicProxy.IInterceptor
{
    public int Count;

    public void Intercept(Castle.DynamicProxy.IInvocation invocation)
    {
        Count++;
        // The method a proxy with a target forwards to is the target's implementation, which Castle
        // finds through the target type's interface map.
        var forwardedTo = invocation.InvocationTarget == null ? "" : " on " + invocation.MethodInvocationTarget.DeclaringType.Name;
        Console.WriteLine("intercept " + invocation.Method.Name + forwardedTo);
        if (invocation.InvocationTarget != null || !invocation.Method.IsAbstract)
        {
            try { invocation.Proceed(); return; } catch (NotImplementedException) { }
        }
        var rt = invocation.Method.ReturnType;
        if (rt != typeof(void) && rt.IsValueType) invocation.ReturnValue = Activator.CreateInstance(rt);
    }
}

public class RepoImpl : IRepo<string>
{
    private readonly Dictionary<int, string> _d = new Dictionary<int, string>();
    public string Get(int id) { return _d[id]; }
    public void Put(int id, string value) { _d[id] = value; }
}

/// A value a scenario computed was not the one it expected.
public sealed class CheckFailed : Exception
{
    public CheckFailed(string message) : base(message) { }
}

public static class Program
{
    static readonly (string Name, Func<int> Run)[] Scenarios =
    {
        ("castle-generic", CastleGeneric),
        ("castle-class", CastleClass),
        ("castle-target", CastleTarget),
        ("moq-basic", MoqBasic),
        ("moq-verifyfail", MoqVerifyFail),
        ("moq-strict", MoqStrict),
        ("nsub-basic", NsubBasic),
        ("nsub-fail", NsubFail),
        ("nsub-class", NsubClass),
        ("fie-callto", FieCallTo),
        ("fie-mhhfail", FieMhhFail),
        ("fie-generic", FieGeneric),
        ("fie-wrap", FieWrap),
    };

    public static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.WriteLine("expected one argument, a scenario's name or `list`; got " + args.Length);
            return 2;
        }

        if (args[0] == "list")
        {
            foreach (var scenario in Scenarios) Console.WriteLine(scenario.Name);
            return 0;
        }

        foreach (var scenario in Scenarios)
        {
            if (scenario.Name != args[0]) continue;
            try
            {
                return scenario.Run();
            }
            catch (CheckFailed e)
            {
                Console.WriteLine("CHECK FAILED: " + e.Message);
                return 1;
            }
            catch (Exception e)
            {
                Console.WriteLine("GUEST EXCEPTION " + e.GetType().FullName + ": " + e.Message);
                return 3;
            }
        }

        Console.WriteLine("unknown scenario " + args[0]);
        return 2;
    }

    static void Expect<T>(T actual, T expected, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new CheckFailed(what + ": expected " + expected + ", got " + actual);
    }

    /// The line by which TestConsumerCases knows that a library's object came from code the library
    /// generated at run time: no assembly on disk defines a type in a dynamic assembly.
    static void PrintGeneratedType(object o)
    {
        var t = o.GetType();
        Console.WriteLine("generated type " + t.FullName + " in a dynamic assembly: " + t.Assembly.IsDynamic);
    }

    static int CastleGeneric()
    {
        var g = new Castle.DynamicProxy.ProxyGenerator();
        var ic = new CountingInterceptor();
        var repo = g.CreateInterfaceProxyWithTarget<IRepo<string>>(new RepoImpl(), ic);
        repo.Put(1, "one");
        var got = repo.Get(1);
        Console.WriteLine(got);
        Expect(got, "one", "the target's value through the proxy");
        var thing = g.CreateInterfaceProxyWithoutTarget<IThing>(ic);
        Console.WriteLine(thing.Echo(5));
        int o;
        Console.WriteLine(thing.TryGet("k", out o) + " " + o);
        Console.WriteLine("count " + ic.Count);
        Expect(ic.Count, 4, "interceptions");
        return 0;
    }

    static int CastleClass()
    {
        var g = new Castle.DynamicProxy.ProxyGenerator();
        var ic = new CountingInterceptor();
        var c = g.CreateClassProxy<Concrete>(ic);
        var sum = c.Add(2, 3);
        Console.WriteLine(sum);
        Expect(sum, 5, "the base method's result through the proxy");
        c.Name = "n";
        Console.WriteLine(c.Name);
        Console.WriteLine(c.Id("s") + c.Id(4));
        Console.WriteLine("count " + ic.Count + " type " + c.GetType().Name);
        PrintGeneratedType(c);
        Expect(ic.Count, 5, "interceptions");
        return 0;
    }

    static int CastleTarget()
    {
        var g = new Castle.DynamicProxy.ProxyGenerator();
        var ic = new CountingInterceptor();
        var opts = new Castle.DynamicProxy.ProxyGenerationOptions();
        opts.AddMixinInstance(new RepoImpl());
        var c = (Concrete)g.CreateClassProxyWithTarget(typeof(Concrete), new Concrete(), opts, ic);
        Console.WriteLine(c.Add(2, 3));
        ((IRepo<string>)c).Put(1, "mixed");
        var got = ((IRepo<string>)c).Get(1);
        Console.WriteLine(got);
        Expect(got, "mixed", "the mixin's value through the proxy");
        Console.WriteLine("count " + ic.Count);
        return 0;
    }

    static int MoqBasic()
    {
        var m = new Moq.Mock<IThing>();
        Moq.Language.Flow.ISetup<IThing, int> s = m.Setup(x => x.Value);
        s.Returns(42);
        var value = m.Object.Value;
        Console.WriteLine("value " + value);
        Expect(value, 42, "the set-up value");
        m.Verify(x => x.Value, Moq.Times.Once());
        Console.WriteLine("verified");
        PrintGeneratedType(m.Object);
        return 0;
    }

    static int MoqVerifyFail()
    {
        var m = new Moq.Mock<IThing>();
        m.Object.Act(3);
        try { m.Verify(x => x.Act(4), Moq.Times.Once()); }
        catch (Moq.MockException e) { Console.WriteLine(e.Message); return 0; }
        throw new CheckFailed("Verify of a call never made did not throw");
    }

    static int MoqStrict()
    {
        var m = new Moq.Mock<IThing>(Moq.MockBehavior.Strict);
        m.Setup(x => x.Value).Returns(1);
        Console.WriteLine(m.Object.Value);
        try { m.Object.Act(1); }
        catch (Moq.MockException e) { Console.WriteLine(e.Message); return 0; }
        throw new CheckFailed("a strict mock accepted a call with no setup");
    }

    static int NsubBasic()
    {
        var s = NSubstitute.Substitute.For<IThing>();
        NSubstitute.SubstituteExtensions.Returns(s.Value, 42);
        Console.WriteLine("value " + s.Value);
        Expect(s.Value, 42, "the configured value");
        s.Act(3);
        NSubstitute.SubstituteExtensions.Received(s).Act(3);
        NSubstitute.SubstituteExtensions.DidNotReceive(s).Act(4);
        Console.WriteLine("received");
        PrintGeneratedType(s);
        return 0;
    }

    static int NsubFail()
    {
        var s = NSubstitute.Substitute.For<IThing>();
        s.Act(3);
        try { NSubstitute.SubstituteExtensions.Received(s).Act(4); }
        catch (NSubstitute.Exceptions.ReceivedCallsException e) { Console.WriteLine(e.Message); return 0; }
        throw new CheckFailed("Received of a call never made did not throw");
    }

    static int NsubClass()
    {
        var s = NSubstitute.Substitute.ForPartsOf<AbstractThing>(5);
        NSubstitute.SubstituteExtensions.Returns(s.Compute(4), 10);
        var twice = s.Twice(4);
        Console.WriteLine("seed " + s.Seed + " twice " + twice);
        Expect(twice, 20, "the base method over the configured abstract one");
        return 0;
    }

    // Issue #2092's snippet.
    static int FieCallTo()
    {
        var fake = A.Fake<IThing>();
        A.CallTo(() => fake.Value).Returns(42);
        Console.WriteLine($"fake.Value = {fake.Value}");
        Expect(fake.Value, 42, "the configured value");
        return 0;
    }

    static int FieMhhFail()
    {
        var fake = A.Fake<IThing>();
        // Ten distinct calls, so that the message pads each call's number to the width of the last
        // one's, which FakeItEasy computes with Math.Log10.
        for (var i = 1; i <= 10; i++) fake.Act(i);
        try { A.CallTo(() => fake.Act(11)).MustHaveHappened(); }
        catch (ExpectationException e)
        {
            Console.WriteLine(e.GetType().FullName);
            Console.WriteLine(e.Message);
            return 0;
        }
        throw new CheckFailed("MustHaveHappened of a call never made did not throw");
    }

    static int FieGeneric()
    {
        var fake = A.Fake<IThing>();
        A.CallTo(() => fake.Echo("a")).Returns("b");
        Console.WriteLine(fake.Echo("a") + " " + fake.Echo(5));
        Expect(fake.Echo("a"), "b", "the configured generic method");
        A.CallTo(fake).Where(c => c.Method.Name == "Describe").WithReturnType<string>().Returns("any");
        Console.WriteLine(fake.Describe(1, "z"));
        PrintGeneratedType(fake);
        return 0;
    }

    static int FieWrap()
    {
        var real = new RepoImpl();
        var fake = A.Fake<IRepo<string>>(x => x.Wrapping(real));
        fake.Put(1, "one");
        Console.WriteLine(fake.Get(1));
        Expect(real.Get(1), "one", "the wrapped object's value");
        A.CallTo(() => fake.Get(1)).MustHaveHappenedOnceExactly();
        var c = A.Fake<AbstractThing>(x => x.WithArgumentsForConstructor(new object[] { 5 }).CallsBaseMethods());
        A.CallTo(() => c.Compute(4)).Returns(10);
        var twice = c.Twice(4);
        Console.WriteLine("twice " + twice);
        Expect(twice, 20, "the base method over the configured abstract one");
        return 0;
    }
}
