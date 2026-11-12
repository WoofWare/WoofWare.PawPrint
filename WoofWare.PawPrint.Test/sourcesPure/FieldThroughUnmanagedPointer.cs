// `ldfld`, `stfld` and `ldflda` whose receiver is an unmanaged pointer: a native int on the
// evaluation stack, which ECMA-335 admits for all three (III.4.10, III.4.28, III.4.11). Each
// pointer here is to a stack local and arrives as a native int from a different opcode:
// `ldelem.i` (an element of a pointer array), `ldind.i` (a pointer read through a `ref`) and
// `conv.u` (a pointer formed from `&local`, then cast through `nint`).
//
// Returns 0 on success, or the number of the first check that failed.
unsafe class Program
{
    struct S
    {
        public int X;
        public long Y;
    }

    static int Main(string[] args)
    {
        S local = new S { X = 5, Y = 7 };

        // `ldelem.i`
        S*[] cells = new S*[1];
        cells[0] = &local;
        if (cells[0]->X != 5) return 1;
        cells[0]->Y = 8;
        if (local.Y != 8) return 2;
        int* viaElement = &cells[0]->X;
        if (*viaElement != 5) return 3;

        // `ldind.i`
        S* p = &local;
        ref S* r = ref p;
        if (r->Y != 8) return 4;
        r->X = 6;
        if (local.X != 6) return 5;
        long* viaIndirect = &r->Y;
        *viaIndirect = 9;
        if (local.Y != 9) return 6;

        // `conv.u`
        nint raw = (nint)(&local);
        if (((S*)raw)->X != 6) return 7;
        ((S*)raw)->X = 10;
        if (local.X != 10) return 8;
        int* viaConv = &((S*)raw)->X;
        if (*viaConv != 10) return 9;

        return 0;
    }
}
