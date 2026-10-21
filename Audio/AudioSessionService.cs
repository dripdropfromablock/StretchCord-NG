using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using StretchCordNG.Services;

namespace StretchCordNG.Audio
{
    /// <summary>
    /// Session-level audio access: "is anything playing on this endpoint" and
    /// "mute/unmute everything owned by these processes".
    ///
    /// NAudio's session wrapper is the one that demonstrably works on real hardware;
    /// hand-rolled Core Audio COM surfaces have historically reported zero sessions where
    /// NAudio listed six, silently breaking the auto-mute.
    /// </summary>
    internal static class AudioSessionService
    {
        private static long _lastMissLogMs;
        private static long _lastFailureLogMs;
        private static string? _lastFailure;

        /// <summary>Rate-limited log so a retry loop cannot flood the log file.</summary>
        private static void LogFailure(string message)
        {
            _lastFailure = message;

            long now = Environment.TickCount64;
            if (now - _lastFailureLogMs < 5000) return;
            _lastFailureLogMs = now;
            AppLog.Write(message);
        }

        /// <summary>True when any session on the endpoint is actively rendering audio.</summary>
        public static bool HasActiveSessions(string? deviceId, out int activeCount)
        {
            activeCount = 0;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in OpenDevices(enumerator, deviceId))
                {
                    try
                    {
                        var sessions = Sessions(device);
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            try
                            {
                                if (sessions[i].State != AudioSessionState.AudioSessionStateActive) continue;
                                if ((int)sessions[i].GetProcessID == Environment.ProcessId) continue;
                                activeCount++;
                            }
                            catch { /* session vanished */ }
                        }
                    }
                    finally
                    {
                        device.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                LogFailure($"audio: endpoint scan failed — {ex.GetType().Name}: {ex.Message}");
            }

            return activeCount > 0;
        }

        /// <summary>
        /// Mutes every audio session owned by the given process ids on every active render
        /// endpoint. Returns the number of sessions whose mute flag could be read back.
        /// </summary>
        public static int MuteProcesses(ICollection<int> pids) => ApplyMute(pids, mute: true);

        public static int UnmuteProcesses(ICollection<int> pids) => ApplyMute(pids, mute: false);

        private static int ApplyMute(ICollection<int> pids, bool mute)
        {
            int changed = 0;
            int endpoints = 0;
            int endpointsWithoutSessions = 0;
            int sessionsSeen = 0;
            var seenPids = new List<int>();

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    endpoints++;
                    try
                    {
                        var sessions = Sessions(device);
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            var session = sessions[i];
                            try
                            {
                                int pid;
                                try { pid = (int)session.GetProcessID; }
                                catch { continue; }

                                sessionsSeen++;
                                if (!seenPids.Contains(pid)) seenPids.Add(pid);
                                if (!pids.Contains(pid)) continue;

                                var volume = session.SimpleAudioVolume;
                                if (volume == null) continue;

                                try
                                {
                                    volume.Mute = mute;
                                    if (volume.Mute == mute) changed++;
                                }
                                catch { /* session went away mid-call */ }
                            }
                            catch { /* ignore individual sessions */ }
                        }
                    }
                    catch
                    {
                        endpointsWithoutSessions++;
                    }
                    finally
                    {
                        device.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                LogFailure($"audio: session scan failed — {ex.GetType().Name}: {ex.Message}");
            }

            // A mute request that matches nothing is the failure the player notices as
            // "the sound is doubled", so record exactly what was on offer. Throttled: the
            // router retries every couple of seconds while it waits for the session.
            if (mute && changed == 0)
            {
                long now = Environment.TickCount64;
                if (now - _lastMissLogMs > 5000)
                {
                    _lastMissLogMs = now;
                    AppLog.Write(
                        $"audio: mute matched nothing — wanted pids=[{string.Join(",", pids)}], " +
                        $"endpoints={endpoints} (no sessions on {endpointsWithoutSessions}), " +
                        $"scanned {sessionsSeen} session(s) with pids=[{string.Join(",", seenPids)}]" +
                        (_lastFailure == null ? "" : $", last failure: {_lastFailure}"));
                }
            }

            return changed;
        }

        /// <summary>The named endpoint when it still exists, otherwise the default one.</summary>
        private static IEnumerable<MMDevice> OpenDevices(MMDeviceEnumerator enumerator, string? deviceId)
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                MMDevice? device = null;
                try { device = enumerator.GetDevice(deviceId); } catch { device = null; }
                if (device != null)
                {
                    yield return device;
                    yield break;
                }
            }

            yield return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        /// <summary>Live session list for an endpoint (RefreshSessions is required for the
        /// session manager to hand back sessions it created before this call).</summary>
        private static SessionCollection Sessions(MMDevice device)
        {
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            return manager.Sessions;
        }
    }
}
