from __future__ import annotations

from typing import Any

import httpx


class DaemonClient:
    def __init__(
        self, base_url: str = "http://127.0.0.1:5087", timeout: float = 15.0
    ) -> None:
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout

    def health(self) -> dict[str, Any]:
        response = httpx.get(f"{self.base_url}/health", timeout=self.timeout)
        response.raise_for_status()
        return response.json()

    def post_observation(self, observation: dict[str, Any]) -> dict[str, Any]:
        response = httpx.post(
            f"{self.base_url}/v1/vision/observations",
            json=observation,
            timeout=self.timeout,
        )
        response.raise_for_status()
        return response.json()
