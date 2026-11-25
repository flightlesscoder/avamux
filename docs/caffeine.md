# Caffeine

The Caffeine page keeps the computer from sleeping, and with `caffeine.keepDisplayOn` (on by default) the screen from
blanking:

- **until you turn it off**;
- **for a while**: 15 minutes to 8 hours, a length (`45m`, `1h30m`) or a clock time (`17:30`). It turns itself off and
  says so when the time is up;
- **while terminals are busy**: whenever a terminal is running a command or an AI agent is working, and for
  `caffeine.graceMinutes` (5) after the last one goes quiet. A terminal at its prompt, or an agent waiting for input,
  does not count. This is the one to use for a long build or an agent left to work overnight: the machine sleeps once
  the work is done.

A dot on the navigation button shows when it is on (green while it is holding the computer awake).

```sh
avamux caffeine on                     # until turned off
avamux caffeine on --for 2h
avamux caffeine on --until 17:30       # also "tomorrow 09:00", +45m
avamux caffeine on --terminals
avamux caffeine status                 # is it on, and why
avamux caffeine off
```

It uses `systemd-inhibit` on Linux, `caffeinate` on macOS and the power API on Windows, and ends when avamux closes,
so quitting never leaves the machine unable to sleep.

| Setting | Default | What it does |
|---|---|---|
| `caffeine.keepDisplayOn` | true | Keep the screen on too, not just stop the computer sleeping. |
| `caffeine.graceMinutes` | 5 | How long "while terminals are busy" holds on after the last one finishes. |
