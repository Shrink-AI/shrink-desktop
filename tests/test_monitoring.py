import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location("monitoring", Path(__file__).parents[1] /
    "plugin/astrbot_plugin_shrink_desktop/monitoring.py")
monitoring = importlib.util.module_from_spec(spec)
spec.loader.exec_module(monitoring)


def record(text="", stop=None, tool=False):
    return {"records": [{"type": "assistant", "message": {
        "content": [{"type": "tool_use"}] if tool else [{"type": "text", "text": text}],
        "stop_reason": stop}}]}


def test_initial_snapshot_and_tool_activity_are_silent():
    assert monitoring.notification(None, record("old result", "end_turn"), 0, 10000) is None
    assert monitoring.notification(record("thinking"), record(tool=True), 0, 10000) is None
    assert monitoring.notification(record(tool=True), record(tool=True), 0, 10000) is None


def test_progress_is_throttled_but_turn_result_is_not():
    assert monitoring.notification(record("first"), record("next"), 9900, 10000) is None
    assert "next" in monitoring.notification(record("first"), record("next"), 100, 10000)
    assert "answer" in monitoring.notification(record("first"), record("answer", "end_turn"), 9900, 10000)
    assert monitoring.notification(record("answer", "end_turn"), record("answer", "end_turn"), 0, 10000) is None


def test_failure_and_confirmation_notify_without_progress_delay():
    for state in ("failed", "waiting_confirmation", "completed"):
        assert monitoring.notification({"state": "running"}, {"state": state}, 9999, 10000)
