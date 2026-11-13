module PrintfCacheHit

// `format` runs the same interpolated string twice. FSC compiles it to a `PrintfFormat`5`
// allocation passed to `PrintfModule.PrintFormatToStringThen`, which parses the format through
// `Cache`4.GetParser`: the first call parses it and the second finds the cached parser.

type Widget = | Widget of int

let format (w : Widget) : string = $"value %O{w} end"

let main (_argv : string array) : int =
    let w1 = Widget 42
    let w2 = Widget 99
    let a = format w1
    let b = format w2

    if a <> "value Widget 42 end" then 1
    elif b <> "value Widget 99 end" then 2
    else 0
