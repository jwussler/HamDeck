using System;
using HamDeck.Models;
using Xunit;

namespace HamDeck.Tests;

public class SessionStatsTests
{
    [Fact]
    public void RecordQSY_IncrementsCount()
    {
        var s = new SessionStats();
        Assert.Equal(0, s.QSYCount);
        s.RecordQSY();
        s.RecordQSY();
        Assert.Equal(2, s.QSYCount);
    }

    [Fact]
    public void RecordBandChange_DoesNotIncrementQSY()
    {
        // Regression guard: QSY was decoupled from band changes. A band change should
        // record the band but NOT bump the QSY count (that's RecordQSY's job now).
        var s = new SessionStats();
        s.RecordBandChange("20m");
        s.RecordBandChange("40m");
        Assert.Equal(0, s.QSYCount);
        Assert.Equal(1, s.BandChanges["20m"]);
        Assert.Equal(1, s.BandChanges["40m"]);
    }

    [Fact]
    public void TxStartEnd_AccumulatesTxTimeAndCountsPtt()
    {
        var s = new SessionStats();
        s.RecordTXStart();
        System.Threading.Thread.Sleep(20);
        s.RecordTXEnd();
        Assert.Equal(1, s.PTTCount);
        Assert.True(s.TotalTXTime > TimeSpan.Zero);
    }

    [Fact]
    public void TxEnd_WithoutStart_IsNoOp()
    {
        var s = new SessionStats();
        s.RecordTXEnd();   // no matching start
        Assert.Equal(TimeSpan.Zero, s.TotalTXTime);
        Assert.Equal(0, s.PTTCount);
    }

    [Fact]
    public void RecordQSO_Counts()
    {
        var s = new SessionStats();
        s.RecordQSO();
        s.RecordQSO();
        Assert.Equal(2, s.QSOCount);
    }
}
