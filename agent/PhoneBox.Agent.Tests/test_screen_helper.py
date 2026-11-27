import importlib.util
import os
import unittest

HELPER_PATH = os.path.join(
    os.path.dirname(__file__), "..", "PhoneBox.Agent", "Linux", "phonebox-screencast.py")
HELPER_PATH = os.path.normpath(HELPER_PATH)

spec = importlib.util.spec_from_file_location("phonebox_screencast", HELPER_PATH)
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)


class FakeBus:
    def __init__(self):
        self.calls = []
        self.closed_sessions = []
        self.closed_requests = []
        self._pending = None
        self._next = 1

    def request(self, interface, method, args, callback):
        handle = self._next
        self._next += 1
        self.calls.append((interface, method, dict(args or {})))
        self._pending = (handle, callback)
        return handle

    def _complete(self, results, code=0):
        handle, cb = self._pending
        self._pending = None
        cb({"code": code, "results": results})

    def complete_create(self, session_handle, code=0):
        self._complete({"session_handle": session_handle}, code)

    def complete_select_devices(self, devices=0, code=0):
        self._complete({"devices": devices}, code)

    def complete_select_sources(self, streams=None, code=0):
        if streams is None:
            streams = [(1, {"size": (1920, 1080), "source_type": 4})]
        self._complete({"streams": streams}, code)

    def complete_start(self, streams=None, code=0):
        if streams is None:
            streams = [(1, {"size": (1920, 1080), "source_type": 4})]
        self._complete({"streams": streams}, code)

    def complete(self, results=None, code=0):
        self._complete(results or {}, code)

    def complete_denied(self):
        self._complete({}, 1)

    def complete_error(self, code=2):
        self._complete({}, code)

    def close_session(self, handle):
        self.closed_sessions.append(handle)

    def close_request(self, handle):
        self.closed_requests.append(handle)


class TestScreenHelper(unittest.TestCase):
    def test_capture_only_does_not_select_input(self):
        bus = FakeBus()
        session = helper.PortalSession(bus, lambda event: None)
        session.start(dict(mode="mirror", control=False, fps=30, bitrate=8000000,
                          cursor="embedded"))
        bus.complete_create("/session/one")
        self.assertEqual(bus.calls[-1][0:2],
                        ("org.freedesktop.portal.ScreenCast", "SelectSources"))

    def test_select_sources_omits_persistence(self):
        bus = FakeBus()
        session = helper.PortalSession(bus, lambda event: None)
        session.start(dict(mode="extend", control=False, fps=30, bitrate=8000000,
                          cursor="embedded"))
        bus.complete_create("/session/one")
        args = bus.calls[-1][2]
        self.assertNotIn("persist_mode", args)
        self.assertNotIn("restore_token", args)

    def test_select_devices_before_select_sources_combined(self):
        bus = FakeBus()
        session = helper.PortalSession(bus, lambda event: None)
        session.start(dict(mode="extend", control=True, fps=30, bitrate=8000000,
                          cursor="embedded"))
        bus.complete_create("/session/one")
        # next should be RemoteDesktop CreateSession, not SelectSources
        self.assertEqual(bus.calls[-1][0:2],
                        ("org.freedesktop.portal.RemoteDesktop", "CreateSession"))
        bus.complete_create("/rd/session")
        self.assertEqual(bus.calls[-1][0:2],
                        ("org.freedesktop.portal.RemoteDesktop", "SelectDevices"))
        # SelectDevices args omit persistence
        self.assertNotIn("persist_mode", bus.calls[-1][2])
        self.assertNotIn("restore_token", bus.calls[-1][2])
        bus.complete_select_devices(devices=helper.PortalSession.POINTER)
        self.assertEqual(bus.calls[-1][0:2],
                        ("org.freedesktop.portal.ScreenCast", "SelectSources"))
        self.assertNotIn("persist_mode", bus.calls[-1][2])
        self.assertNotIn("restore_token", bus.calls[-1][2])

    def test_granted_device_check(self):
        bus = FakeBus()
        events = []
        session = helper.PortalSession(bus, events.append)
        session.start(dict(mode="extend", control=True, fps=30, bitrate=8000000,
                          cursor="embedded"))
        bus.complete_create("/session/one")
        bus.complete_create("/rd/session")
        bus.complete_select_devices(devices=0)  # no pointer granted
        bus.complete_select_sources()
        bus.complete_start()
        self.assertFalse(events[-1]["inputAvailable"])

    def test_input_available_when_granted(self):
        bus = FakeBus()
        events = []
        session = helper.PortalSession(bus, events.append)
        session.start(dict(mode="extend", control=True, fps=30, bitrate=8000000,
                          cursor="embedded"))
        bus.complete_create("/session/one")
        bus.complete_create("/rd/session")
        bus.complete_select_devices(devices=helper.PortalSession.POINTER)
        bus.complete_select_sources()
        bus.complete_start()
        self.assertTrue(events[-1]["inputAvailable"])

    def test_consent_each_new_session(self):
        # two separate sessions both require CreateSession (no token reuse)
        for _ in range(2):
            bus = FakeBus()
            session = helper.PortalSession(bus, lambda event: None)
            session.start(dict(mode="extend", control=False, fps=30, bitrate=8000000,
                              cursor="embedded"))
            self.assertEqual(bus.calls[0][1], "CreateSession")

    def test_denial_without_retry(self):
        bus = FakeBus()
        events = []
        session = helper.PortalSession(bus, events.append)
        session.start(dict(mode="extend", control=False, fps=30, bitrate=8000000,
                          cursor="embedded"))
        bus.complete_denied()
        self.assertEqual(session.state, "denied")
        # no automatic retry: still one CreateSession call
        creates = [c for c in bus.calls if c[1] == "CreateSession"]
        self.assertEqual(len(creates), 1)

    def test_cancellation_during_approval(self):
        bus = FakeBus()
        session = helper.PortalSession(bus, lambda event: None)
        session.start(dict(mode="extend", control=False, fps=30, bitrate=8000000,
                          cursor="embedded"))
        session.cancel()
        self.assertTrue(len(bus.closed_requests) >= 1)
        self.assertEqual(bus.closed_sessions, [])

    def test_media_reset_does_not_create_session(self):
        bus = FakeBus()
        session = helper.PortalSession(bus, lambda event: None)
        session.start(dict(mode="extend", control=False, fps=30, bitrate=8000000,
                          cursor="embedded"))
        bus.complete_create("/session/one")
        bus.complete_select_sources()
        bus.complete_start()
        before = len([c for c in bus.calls if c[1] == "CreateSession"])
        session.reset_media(1)
        after = len([c for c in bus.calls if c[1] == "CreateSession"])
        self.assertEqual(before, after)

    def test_late_response_after_stop(self):
        bus = FakeBus()
        session = helper.PortalSession(bus, lambda event: None)
        session.start(dict(mode="extend", control=False, fps=30, bitrate=8000000,
                          cursor="embedded"))
        session.stop()
        # completing the (now cancelled) create should not crash
        try:
            bus.complete_create("/session/one")
        except Exception as e:
            self.fail(str(e))

    def test_absent_size_falls_back(self):
        bus = FakeBus()
        events = []
        session = helper.PortalSession(bus, events.append)
        session.start(dict(mode="extend", control=False, fps=30, bitrate=8000000,
                          cursor="embedded"))
        bus.complete_create("/session/one")
        bus.complete_select_sources(streams=[(1, {})])
        bus.complete_start(streams=[(1, {})])
        self.assertEqual(events[-1]["width"], 1920)
        self.assertEqual(events[-1]["height"], 1080)

    def test_stop_closes_session_once(self):
        bus = FakeBus()
        session = helper.PortalSession(bus, lambda event: None)
        session.start(dict(mode="extend", control=False, fps=30, bitrate=8000000,
                          cursor="embedded"))
        bus.complete_create("/session/one")
        bus.complete_select_sources()
        bus.complete_start()
        session.stop()
        self.assertEqual(len(bus.closed_sessions), 1)
        session.stop()
        self.assertEqual(len(bus.closed_sessions), 1)


if __name__ == "__main__":
    unittest.main()
