namespace WoofWare.PawPrint.Test

open System
open System.Reflection
open System.Reflection.Emit
open NUnit.Framework

/// Events in an image on disk with what no compiler writes: a raise method, other methods, event flags
/// (`0x0600`, and every bit), and two events of one name. The type has 25 events, more than
/// `MetadataImport.Enum`'s sixteen-entry inline buffer, and 32 MethodSemantics rows, among which CoreCLR's
/// read-only metadata finds an event's rows in RID order. The image is written by `PersistedAssemblyBuilder`,
/// whose `EventBuilder` keeps one add, one remove and one raise method, a second replacing the first.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestFabricatedEventAccessors =

    /// The name of the fabricated assembly, and of its type, `FabEvents`.
    let internal assemblyName : string = "FabEvents"

    /// Writes the image. `FabEvents`' events, in row order: `Ev` (flags `SpecialName | RTSpecialName`, an add,
    /// a remove, a raise, and other methods `otherB` then `otherA`); two named `Dup`, with adds `add_Dup1` and
    /// `add_Dup2`; `Many` (add `a1` replaced by `a2`, others `o1`, `o2`, `o3`); `Flagged` (flags `0xFFFF`, an
    /// add); and `F0` to `F19`, an add each.
    let internal fabricate () : byte[] =
        let builder =
            PersistedAssemblyBuilder (AssemblyName assemblyName, typeof<obj>.Assembly)

        let modul = builder.DefineDynamicModule assemblyName

        let t =
            modul.DefineType ("FabEvents", TypeAttributes.Public ||| TypeAttributes.Class, typeof<obj>)

        t.DefineDefaultConstructor MethodAttributes.Public |> ignore<ConstructorBuilder>

        let handler = t.DefineField ("handler", typeof<Action>, FieldAttributes.Public)

        // An accessor taking an `Action`, storing it in `handler` when `store` is set.
        let accessor (name : string) (store : bool) : MethodBuilder =
            let method =
                t.DefineMethod (
                    name,
                    MethodAttributes.Public
                    ||| MethodAttributes.SpecialName
                    ||| MethodAttributes.HideBySig,
                    typeof<Void>,
                    [| typeof<Action> |]
                )

            let il = method.GetILGenerator ()

            if store then
                il.Emit OpCodes.Ldarg_0
                il.Emit OpCodes.Ldarg_1
                il.Emit (OpCodes.Stfld, handler)

            il.Emit OpCodes.Ret
            method

        let plain (name : string) : MethodBuilder =
            let method =
                t.DefineMethod (name, MethodAttributes.Public, typeof<Void>, Type.EmptyTypes)

            method.GetILGenerator().Emit OpCodes.Ret
            method

        let ev =
            t.DefineEvent ("Ev", EventAttributes.SpecialName ||| EventAttributes.RTSpecialName, typeof<Action>)

        let raise =
            t.DefineMethod (
                "raise_Ev",
                MethodAttributes.Public ||| MethodAttributes.SpecialName,
                typeof<Void>,
                Type.EmptyTypes
            )

        let il = raise.GetILGenerator ()
        il.Emit OpCodes.Ldarg_0
        il.Emit (OpCodes.Ldfld, handler)
        il.Emit (OpCodes.Callvirt, typeof<Action>.GetMethod "Invoke")
        il.Emit OpCodes.Ret

        ev.SetAddOnMethod (accessor "add_Ev" true)
        ev.SetRemoveOnMethod (accessor "remove_Ev" false)
        ev.SetRaiseMethod raise
        ev.AddOtherMethod (plain "otherB")
        ev.AddOtherMethod (plain "otherA")

        let dup1 = t.DefineEvent ("Dup", EventAttributes.None, typeof<Action>)
        dup1.SetAddOnMethod (accessor "add_Dup1" false)
        let dup2 = t.DefineEvent ("Dup", EventAttributes.None, typeof<Action>)
        dup2.SetAddOnMethod (accessor "add_Dup2" false)

        let many = t.DefineEvent ("Many", EventAttributes.None, typeof<Action>)
        let a1 = accessor "a1" false
        let a2 = accessor "a2" false
        let o1 = plain "o1"
        let o2 = plain "o2"
        let o3 = plain "o3"
        many.SetAddOnMethod a1
        many.AddOtherMethod o1
        many.SetAddOnMethod a2
        many.AddOtherMethod o2
        many.AddOtherMethod o3

        let flagged =
            t.DefineEvent ("Flagged", enum<EventAttributes> 0xFFFF, typeof<Action>)

        flagged.SetAddOnMethod (accessor "add_Flagged" false)

        for i in 0..19 do
            let filler = t.DefineEvent ($"F%d{i}", EventAttributes.None, typeof<Action>)
            filler.SetAddOnMethod (accessor $"add_F%d{i}" false)

        t.CreateType () |> ignore<Type>

        use image = new IO.MemoryStream ()
        builder.Save image
        image.ToArray ()

    let private driver =
        """
using System;
using System.Linq;
using System.Reflection;

public static class Driver
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    static string N(MethodInfo m) => m == null ? "null" : m.Name;

    static string Others(EventInfo e) => string.Join(",", e.GetOtherMethods(true).Select(m => m.Name));

    public static int Main()
    {
        Type t = typeof(FabEvents);
        EventInfo[] events = t.GetEvents(All);

        // The second `Dup` is dropped by name, by `RuntimeType.PopulateEvents`.
        string expected = "Ev,Dup,Many,Flagged," + string.Join(",", Enumerable.Range(0, 20).Select(i => "F" + i));
        string names = string.Join(",", events.Select(e => e.Name));
        if (names != expected)
        {
            Console.WriteLine(names);
            return 1;
        }

        int first = events[0].MetadataToken;
        if ((first & unchecked((int)0xFF000000)) != 0x14000000) return 2;
        if (events[1].MetadataToken != first + 1 || events[2].MetadataToken != first + 3) return 3;
        for (int i = 3; i < events.Length; i++)
        {
            if (events[i].MetadataToken != first + i + 1) return 4;
        }

        EventInfo ev = events[0];
        if ((int)ev.Attributes != 0x0600 || !ev.IsSpecialName) return 5;
        if (N(ev.GetAddMethod(true)) != "add_Ev" || N(ev.GetRemoveMethod(true)) != "remove_Ev") return 6;
        if (N(ev.GetRaiseMethod(true)) != "raise_Ev" || N(ev.RaiseMethod) != "raise_Ev") return 7;
        if (Others(ev) != "otherB,otherA") return 8;

        if (N(t.GetEvent("Dup").GetAddMethod()) != "add_Dup1") return 9;

        EventInfo many = events[2];
        if (N(many.GetAddMethod(true)) != "a2" || N(many.GetRemoveMethod(true)) != "null") return 10;
        if (Others(many) != "o1,o2,o3") return 11;

        EventInfo flagged = events[3];
        if ((int)flagged.Attributes != 0xFFFF || !flagged.IsSpecialName) return 12;
        if (N(flagged.GetAddMethod(true)) != "add_Flagged") return 13;

        for (int i = 4; i < events.Length; i++)
        {
            EventInfo e = events[i];
            if (e.Attributes != EventAttributes.None || N(e.GetAddMethod(true)) != "add_" + e.Name || Others(e) != "") return 14;
        }

        if (t.GetEvent("F19") == null) return 15;

        object o = Activator.CreateInstance(t);
        int hits = 0;
        ev.AddEventHandler(o, new Action(() => hits++));
        ev.GetRaiseMethod(true).Invoke(o, null);
        if (hits != 1) return 16;

        return 0;
    }
}
"""

    [<Test>]
    let ``events of an image on disk, with raise and other methods, flags and a repeated name`` () : unit =
        FabricatedGuest.run assemblyName (fabricate ()) "FabEventsDriver" driver 0
