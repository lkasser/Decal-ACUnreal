namespace Decal.Adapter.NetParser
{
    // The compiled form of Decal's messages.xml: one MemberParser per field, vector, case or
    // type, chained through Next and nested through Child. Named, laid out and placed in this
    // namespace exactly as Decal's own were, because plugins reached in by reflection - Virindi
    // Tank's diagnostics built a Message from Message.GetParser and the (byte[], MemberParser)
    // constructor to tell whether a message fitted the schema - and must still find them.

    /// <summary>How a member's condition compares the field it tests. Only EQ and NE are ever made.</summary>
    internal enum MemberParserCondition
    {
        None,
        EQ,
        NE,
        GE,
        GT,
        LE,
        LT,
    }

    /// <summary>What a member reads: one of the schema's primitives, a struct, a vector, or a case of a switch or maskmap.</summary>
    internal enum MemberParserType
    {
        BYTE,
        WORD,
        PackedWORD,
        DWORD,
        PackedDWORD,
        QWORD,
        @float,
        @double,
        String,
        WString,
        Struct,
        Vector,
        Case,
    }

    /// <summary>One member of a message or type, with what decides whether it is there and how long it is.</summary>
    internal class MemberParser
    {
        /// <summary>The member after this one at the same level.</summary>
        public MemberParser Next;

        /// <summary>A struct's or vector element's members, or a case's.</summary>
        public MemberParser Child;

        public MemberParserType MemberType;

        public string MemberName;

        /// <summary>None for a member that is always there; otherwise how <see cref="ConditionField"/> is tested.</summary>
        public MemberParserCondition Condition;

        /// <summary>The field the condition tests, looked for here first and then in each enclosing struct.</summary>
        public string ConditionField;

        /// <summary>XORed into the tested value before the mask: a maskmap whose bits mean "absent" says so with this.</summary>
        public long ConditionXor;

        public long ConditionAnd;

        public long ConditionResult;

        /// <summary>A vector's length field, found as <see cref="ConditionField"/> is.</summary>
        public string LengthField;

        /// <summary>The bits of the length field that hold the length, shifted down to bit 0.</summary>
        public long LengthMask;

        /// <summary>Added to the length: a vector's "skip" subtracts the entries a length counts that are not in it.</summary>
        public int LengthDelta;

        /// <summary>Where the member starts is rounded up to this many bytes; zero for nowhere in particular.</summary>
        public int PreAlignment;

        /// <summary>Where the next member starts is rounded up to this many bytes, whether this member was there or not.</summary>
        public int PostAlignment;

        public MemberParser()
        {
        }

        /// <summary>A copy, for a field of a named type: the type's members, under the field's name.</summary>
        public MemberParser(MemberParser source)
        {
            Next = source.Next;
            Child = source.Child;
            MemberType = source.MemberType;
            MemberName = source.MemberName;
            Condition = source.Condition;
            ConditionField = source.ConditionField;
            ConditionXor = source.ConditionXor;
            ConditionAnd = source.ConditionAnd;
            ConditionResult = source.ConditionResult;
            LengthField = source.LengthField;
            LengthMask = source.LengthMask;
            LengthDelta = source.LengthDelta;
            PreAlignment = source.PreAlignment;
            PostAlignment = source.PostAlignment;
        }
    }
}
