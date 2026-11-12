// A pointer stored through a `fixed` pointer into a pointer-element array.
//
// `fixed (S** p = array)` pins the first element with `ldelema` and converts the byref with
// `conv.u`, so the `*p = ...` stores are `stind.i` through a byte cursor over the array's cells.
// Each store covers exactly one cell, and the cell must still hold a pointer afterwards, which
// the element reads (`ldelem.i`) and dereferences (`ldfld`) below check.
unsafe class Program
{
    struct S
    {
        public int X;
    }

    static int Main(string[] args)
    {
        S first = new S { X = 11 };
        S second = new S { X = 22 };

        S*[] cells = new S*[2];

        fixed (S** p = cells)
        {
            *p = &first;
            p[1] = &second;
        }

        if (cells[0]->X != 11) return 1;
        if (cells[1]->X != 22) return 2;
        if (cells[0] != &first) return 3;

        fixed (S** p = cells)
        {
            p[1] = null;
        }

        if (cells[1] != null) return 4;
        if (cells[0]->X != 11) return 5;

        return 0;
    }
}
