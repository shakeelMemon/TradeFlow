namespace TradeFlow.Fix;

/// <summary>FIX 4.4 tag numbers used by TradeFlow.</summary>
public static class Tags
{
    public const int Account = 1;
    public const int AvgPx = 6;
    public const int BeginString = 8;
    public const int BodyLength = 9;
    public const int CheckSum = 10;
    public const int ClOrdID = 11;
    public const int CumQty = 14;
    public const int Currency = 15;
    public const int ExecID = 17;
    public const int LastPx = 31;
    public const int LastQty = 32;
    public const int MsgSeqNum = 34;
    public const int MsgType = 35;
    public const int OrderID = 37;
    public const int OrderQty = 38;
    public const int OrdStatus = 39;
    public const int OrdType = 40;
    public const int Price = 44;
    public const int SenderCompID = 49;
    public const int SendingTime = 52;
    public const int Side = 54;
    public const int Symbol = 55;
    public const int TargetCompID = 56;
    public const int Text = 58;
    public const int TimeInForce = 59;
    public const int TransactTime = 60;
    public const int EncryptMethod = 98;
    public const int HeartBtInt = 108;
    public const int TestReqID = 112;
    public const int ResetSeqNumFlag = 141;
    public const int ExecType = 150;
    public const int LeavesQty = 151;
    public const int SecondaryClOrdID = 526;
}

/// <summary>FIX message types (tag 35).</summary>
public static class MsgTypes
{
    public const string Heartbeat = "0";
    public const string TestRequest = "1";
    public const string Reject = "3";
    public const string Logout = "5";
    public const string ExecutionReport = "8";
    public const string Logon = "A";
    public const string NewOrderSingle = "D";

    public static string Name(string msgType) => msgType switch
    {
        Heartbeat => "Heartbeat",
        TestRequest => "TestRequest",
        Reject => "Reject",
        Logout => "Logout",
        ExecutionReport => "ExecutionReport",
        Logon => "Logon",
        NewOrderSingle => "NewOrderSingle",
        _ => "MsgType " + msgType,
    };
}

/// <summary>ExecType (tag 150) values used here.</summary>
public static class ExecTypes
{
    public const string New = "0";
    public const string Rejected = "8";
    public const string Trade = "F";
}

/// <summary>OrdStatus (tag 39) values used here.</summary>
public static class OrdStatuses
{
    public const string New = "0";
    public const string PartiallyFilled = "1";
    public const string Filled = "2";
    public const string Rejected = "8";
}
