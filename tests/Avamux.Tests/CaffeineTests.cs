using Avamux.Core.Awake;
using Avamux.Core.Config;

namespace Avamux.Tests;

public class CaffeineTests
{
    private sealed class FakeInhibitor : ISleepInhibitor
    {
        public int Holds;
        public bool Display;
        public bool Holding { get; private set; }
        public string? Hold(string why, bool display) { Holds++; Holding = true; Display = display; return null; }
        public void Release() => Holding = false;
        public void Dispose() => Release();
    }

    private sealed class World
    {
        public DateTime Now = new(2026, 11, 14, 9, 0, 0);
        public List<string> Busy = [];
        public CaffeineSettings Settings = new() { GraceMinutes = 5 };
        public readonly FakeInhibitor Inhibitor = new();
        public readonly CaffeineService Service;
        public World() => Service = new CaffeineService(() => Busy.ToList(), () => Settings, Inhibitor, () => Now);
    }

    [Fact]
    public void Indefinite_holds_until_turned_off()
    {
        var w = new World();
        Assert.False(w.Inhibitor.Holding);
        w.Service.Indefinite();
        Assert.True(w.Inhibitor.Holding);
        Assert.True(w.Inhibitor.Display);
        w.Now = w.Now.AddDays(3);
        w.Service.Tick();
        Assert.True(w.Service.Status.Holding);
        w.Service.Off();
        Assert.False(w.Inhibitor.Holding);
        Assert.Equal(CaffeineMode.Off, w.Service.Mode);
    }

    [Fact]
    public void A_timed_session_turns_itself_off()
    {
        var w = new World();
        var expired = 0;
        w.Service.Expired += () => expired++;
        w.Service.For(TimeSpan.FromMinutes(30));
        Assert.Contains("30m 00s", w.Service.Status.Describe(w.Now));
        w.Now = w.Now.AddMinutes(29);
        w.Service.Tick();
        Assert.True(w.Inhibitor.Holding);
        w.Now = w.Now.AddMinutes(2);
        w.Service.Tick();
        Assert.False(w.Inhibitor.Holding);
        Assert.Equal(CaffeineMode.Off, w.Service.Mode);
        Assert.Equal(1, expired);
        Assert.Throws<ArgumentException>(() => w.Service.For(TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => w.Service.UntilTime(w.Now.AddMinutes(-1)));
    }

    [Fact]
    public void Terminal_mode_holds_while_busy_and_for_the_grace_period_after()
    {
        var w = new World();
        w.Service.WhileTerminalsBusy();
        Assert.False(w.Inhibitor.Holding); // nothing running yet
        Assert.Contains("Waiting", w.Service.Status.Describe(w.Now));

        w.Busy.Add("build");
        w.Service.Tick();
        Assert.True(w.Inhibitor.Holding);
        Assert.Equal(["build"], w.Service.Status.BusyTerminals);
        Assert.Contains("build is busy", w.Service.Status.Describe(w.Now));

        w.Busy.Clear();
        w.Now = w.Now.AddMinutes(4);
        w.Service.Tick();
        Assert.True(w.Inhibitor.Holding); // still in the grace period
        Assert.Contains("staying awake", w.Service.Status.Describe(w.Now));
        w.Now = w.Now.AddMinutes(2);
        w.Service.Tick();
        Assert.False(w.Inhibitor.Holding);
        Assert.Equal(CaffeineMode.Terminals, w.Service.Mode); // still armed for the next job

        w.Busy.Add("claude");
        w.Service.Tick();
        Assert.True(w.Inhibitor.Holding);
    }

    [Fact]
    public void Changing_the_screen_setting_takes_effect_while_holding()
    {
        var w = new World();
        w.Service.Indefinite();
        Assert.True(w.Inhibitor.Display);
        w.Settings = new CaffeineSettings { KeepDisplayOn = false };
        w.Service.Tick();
        Assert.True(w.Inhibitor.Holding);
        Assert.False(w.Inhibitor.Display);
        Assert.Equal(2, w.Inhibitor.Holds);
    }
}
