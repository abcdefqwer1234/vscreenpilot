using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using Xunit;

namespace VScreenPilot.Tests;

[Collection("ProfileEnvironment")]
public sealed class StateStoreV2Tests
{
    [Fact]
    public void V2RoundTripsRecoveryFields()
    {
        string? previous = Environment.GetEnvironmentVariable(Profile.EnvironmentVariable);
        string profile = "state-v2-" + Guid.NewGuid().ToString("N")[..10];
        Environment.SetEnvironmentVariable(Profile.EnvironmentVariable, profile);
        StateStore? store = null;

        try
        {
            store = new StateStore(new LogRing(capacity: 10, fileEnabled: false));
            var state = new SessionState
            {
                Pid = 1234,
                StartedAt = "2026-11-27T10:00:00Z",
                DisplayIndex = 2,
                DisplayDevice = @"\\.\DISPLAY9",
            };
            state.Apps.Add(new AppState
            {
                Pid = 4321,
                ExePath = @"C:\Tools\demo.exe",
                Hwnd = 0x1234,
                OwnerClientId = "",
                Dedicated = true,
                Detached = true,
                TempProfile = @"C:\Temp\vsp-profile",
                ClientKey = "3f2a1b9c8d7e6f50",
            });

            store.Save(state);
            SessionState? loaded = store.Load();

            Assert.NotNull(loaded);
            Assert.Equal(2, loaded!.DisplayIndex);
            Assert.Single(loaded.Apps);
            Assert.True(loaded.Apps[0].Detached);
            Assert.Equal("3f2a1b9c8d7e6f50", loaded.Apps[0].ClientKey);
            Assert.Equal(@"C:\Temp\vsp-profile", loaded.Apps[0].TempProfile);
            Assert.NotEmpty(loaded.SavedAt);
        }
        finally
        {
            store?.Clear();
            Environment.SetEnvironmentVariable(Profile.EnvironmentVariable, previous);
        }
    }
}
