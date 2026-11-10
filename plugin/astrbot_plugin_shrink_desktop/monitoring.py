"""Select observable milestones without treating log activity as task completion."""
import json

PROGRESS_INTERVAL = 30 * 60


def assistant_update(result):
    latest = None
    for raw in result.get("records", []):
        try:
            row = json.loads(raw) if isinstance(raw, str) else raw
            if not isinstance(row, dict):
                continue
            message = row.get("message") or row.get("payload") or row
            if not isinstance(message, dict):
                continue
            role = message.get("role") or row.get("type")
            if role in {"user", "tool", "tool_result"}:
                latest = None
            elif role == "assistant":
                content = message.get("content", [])
                if isinstance(content, str):
                    content = [{"type": "text", "text": content}]
                if not isinstance(content, list):
                    continue
                if any(part.get("type") in {"tool_use", "function_call"} for part in content if isinstance(part, dict)):
                    latest = None
                    continue
                text = "\n".join(part.get("text", "") for part in content
                                 if isinstance(part, dict) and part.get("type") in {"text", "output_text"}).strip()
                latest = (text, message.get("stop_reason") == "end_turn") if text else None
        except (ValueError, TypeError, AttributeError):
            continue
    return latest


def notification(previous, current, last_sent, now):
    if not previous:
        return None
    state = current.get("state")
    if state != previous.get("state") and state in {"waiting_confirmation", "waiting_approval", "failed", "interrupted", "completed"}:
        return "监工状态发生变化：" + json.dumps(current, ensure_ascii=False)[-16000:]
    update = assistant_update(current)
    if not update or update == assistant_update(previous):
        return None
    text, ended_turn = update
    if not ended_turn and now - last_sent < PROGRESS_INTERVAL:
        return None
    return ("监工观察到 Agent 的本轮答复；仅观察日志，尚未验证整个任务完成。"
            if ended_turn else "监工阶段进度（已合并普通日志变化，尚未验证任务完成）：") + text[-12000:]
