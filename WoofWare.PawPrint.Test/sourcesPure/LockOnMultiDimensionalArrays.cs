using System;
using System.Threading;

// A multi-dimensional array's header word sits as far below its elements as its rank makes it:
// below the element data are one int32 length and one int32 lower bound per dimension, then the
// array's length and padding, then the method-table pointer. On .NET 11, `lock` reaches that word
// from the array's raw data, so locking arrays of several ranks places it for each.
public class Program
{
    private static int Exercise(Array array, Array other, int code)
    {
        lock (array)
        {
            if (!Monitor.IsEntered(array)) return code;
            if (Monitor.IsEntered(other)) return code + 1;

            lock (array)
            {
                if (!Monitor.IsEntered(array)) return code + 2;
            }

            if (!Monitor.IsEntered(array)) return code + 3;
        }

        if (Monitor.IsEntered(array)) return code + 4;

        return 0;
    }

    public static int Main(string[] args)
    {
        int[,] rankTwo = new int[2, 3];
        long[,,] rankThree = new long[2, 1, 2];
        byte[,,,] rankFour = new byte[1, 2, 1, 2];

        int r = Exercise(rankTwo, rankThree, 10);
        if (r != 0) return r;
        r = Exercise(rankThree, rankFour, 20);
        if (r != 0) return r;
        r = Exercise(rankFour, rankTwo, 30);
        if (r != 0) return r;

        // The elements are where they were.
        rankTwo[1, 2] = 7;
        rankThree[1, 0, 1] = 9;
        if (rankTwo[1, 2] != 7 || rankThree[1, 0, 1] != 9) return 40;

        return 0;
    }
}
