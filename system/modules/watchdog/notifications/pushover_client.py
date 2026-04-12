"""
Pushover API client for Watchdog notifications.

Matches Robot's PushoverClient behavior: same endpoint, priority mapping.
Uses urllib for no extra dependencies.
"""
import logging
import urllib.error
import urllib.parse
import urllib.request
from typing import Optional, Tuple

logger = logging.getLogger(__name__)

PUSHOVER_ENDPOINT = "https://api.pushover.net/1/messages.json"
DEFAULT_TIMEOUT = 12


def pushover_send(
    user_key: str,
    app_token: str,
    title: str,
    message: str,
    priority: int = 0,
    timeout: float = DEFAULT_TIMEOUT,
) -> Tuple[bool, Optional[int], Optional[str]]:
    """
    Send a push notification via Pushover API.

    Args:
        user_key: Pushover user key
        app_token: Pushover application token
        title: Notification title
        message: Notification message body
        priority: 0=normal, 1=high, 2=emergency
        timeout: Request timeout in seconds

    Returns:
        Tuple of (success, http_status_code, error_message)
    """
    if not user_key or not app_token:
        return False, None, "User key or app token is empty"

    data = {
        "token": app_token,
        "user": user_key,
        "title": title[:250] if title else "",
        "message": message[:1024] if message else "",
        "priority": priority,
    }
    if priority == 2:
        data["expire"] = 3600
        data["retry"] = 60

    body = urllib.parse.urlencode(data).encode("utf-8")
    req = urllib.request.Request(
        PUSHOVER_ENDPOINT,
        data=body,
        method="POST",
        headers={"Content-Type": "application/x-www-form-urlencoded"},
    )

    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            status = resp.getcode()
            if 200 <= status < 300:
                return True, status, None
            return False, status, f"HTTP {status}"
    except urllib.error.HTTPError as e:
        return False, e.code, str(e)
    except urllib.error.URLError as e:
        return False, None, str(e.reason) if e.reason else str(e)
    except TimeoutError as e:
        return False, None, str(e)
    except Exception as e:
        logger.exception("Pushover send failed")
        return False, None, str(e)
