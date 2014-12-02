LsonLib
=======

LsonLib is a pure C# library for parsing and stringifying data in the "Lua variable list" format, hereby dubbed "LSON" (for "Lua Syntax Object Notation").


Basic Usage
-----------

This library parses strings consisting of Lua variable assignments, such as this one:

    VAR_1 = { "Foo", ["Bar"] = "Baz" }
    VAR_2 = { ["Int"] = 235, ["Null"] = nil, ["StrInt"] = "123" }

Strings are parsed by calling `LsonVars.Parse`, which returns a .NET dictionary of `LsonValue` objects:

    var d = LsonVars.Parse(File.ReadAllText(somefile));
    var myVar1 = d["VAR_1"];    // myVar is typed LsonValue
    var myVar2 = d["VAR_2"];

    File.WriteAllText(somefile, LsonVars.ToString(d)); // serialize back to a file

All LSON values are stored as objects deriving from `LsonValue`. The following types of values are supported: `LsonBool`, `LsonNumber`, `LsonString`, `LsonDict`. `nil` values are stored as the .NET `null` reference.

`LsonValue` implements all the methods and indexers applicable to all the different value types. By default, all operations are strict (non-lenient): if the underlying type does not directly support the required operation, an exception is thrown:

    var a = myVar1["Bar"];  // a is typed LsonValue, and contains an LsonString instance for "Baz"
    var b = a["Bar"];      // exception: a must be an LsonDict instance

To extract the underlying value, call one of the `Get*` methods:

    var x = myVar1["Bar"].GetString();  // returns the .NET string "Baz"
    var y = myVar2["Int"].GetInt();     // returns the integer 235
    var z = myVar2["StrInt"].GetInt();  // exception: "123" is a string, and GetInt is strict

Explicit casts to .NET types have the same effect, but also support null:

    var x = (string) myVar1["Bar"];
    var y = (int) myVar2["Int"];
    var z = (int) myVar2["StrInt"];       // still throws: explicit casts are also strict

    int f = myVar2[1];                    // compile error: no implicit casts from LsonValue

    var a = (int?) myVar2["Int"];         // a is 235
    var b = (int?) myVar2["Null"];        // b is null
    var c = (bool?) myVar2["Null"];       // c is null: null is compatible with all the types
    var d = (string) myVar2["Null"];      // d is also null

For completeness, `GetDict()` is a method that returns the current value as `LsonDict` if it's a dictionary, and throws otherwise -- just like all the other strict Get* methods.

LSON values can be manipulated in all the expected ways:

    myVar1.Remove("Bar");    // removes the value under the "Bar" key
    myVar1.Remove(1);        // same, using 1 as the key
    myVar1["Stuff"] = 25;
    myVar1[7] = "seven";
    myVar1[8] = new LsonDict { { 1, "one" }, { 2, "two" } };
    myVar1["half"] = 0.5;
    myVar1["nothing"] = null;
    myVar1["yes"] = true;
    myVar1.Add("key", "value");

    foreach (var kvp in myVar1)
        // use kvp.Key and kvp.Value

The above example utilises implicit casts from strings, bools and numeric types to `LsonString`, `LsonBool` etc.

Note that `LsonDict` keys are actually `LsonValue`s themselves. Just like Lua tables, an `LsonDict` can be indexed by integer or string (or, less usefully, bool or another dictionary).

The `LsonNumber` class is capable of representing all signed 64-bit integers, as well as all doubles. If the value is a whole number and fits in the range of the type `long`, the number is stored in a `long` field. Otherwise it's stored in a `double`.


"Lenient" and "Safe"
--------------------

If you don't want a type incompatibility to result in an exception, use the Get*Lenient methods:

    var x = myVar2["Int"].GetStringLenient();  // returns the string "235"
    var y = myVar2["StrInt"].GetIntLenient();  // returns the integer 123

Lenient methods still throw if the operation is impossible:

    var a = myVar2["Null"].GetIntLenient();    // throws: nil is not convertible to an integer
    var b = myVar1["Bar"].GetIntLenient();     // throws: the string "Baz" cannot be parsed as int

If you prefer to get a `null` instead of an exception on failure, use the Get*Safe methods. "Safe" and "Lenient" are orthogonal, so `GetIntSafe` will not convert a string even if it's parseable; to do so use `GetIntLenientSafe`:

    var b = myVar1["Bar"].GetIntSafe();            // returns null: "Baz" is a string, not a number
    var c = myVar2["StrInt"].GetIntSafe();         // returns null: "123" is a string, not a number
    var d = myVar2["StrInt"].GetIntLenientSafe();  // returns 123

These method names are long for a reason. This library positions the strict methods as the primary way to access LSON because in the opinion of the author, excessive leniency increases maintenance costs.

It is also possible to suppress exceptions for missing dictionary keys:

    var a = myVar1["Bar"]["Foo"].GetString();       // throws on "Foo": the object is a string
    var b = myVar1["Bar"].Safe["Foo"].GetString();  // returns null instead of throwing


Credits
-------

This library is more or less a direct port of RT.Util's JSON manipulation library, which is not yet available publicly.
