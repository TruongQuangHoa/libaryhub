from __future__ import annotations

import json
from functools import lru_cache
from pathlib import Path
from typing import Any

from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict


BASE_DIR = Path(__file__).resolve().parents[1]


class Settings(BaseSettings):
    """Cấu hình runtime cho Chatbot Python.

    Giá trị được nạp từ biến môi trường hoặc file .env trong thư mục Chatbot.
    """

    model_config = SettingsConfigDict(
        env_file=str(BASE_DIR / ".env"),
        env_file_encoding="utf-8",
        extra="ignore",
        case_sensitive=False,
    )

    app_name: str = "LibraryHub Chatbot"
    app_env: str = "development"
    host: str = "127.0.0.1"
    port: int = 8010

    # Bảo mật tích hợp ASP.NET -> Python
    chatbot_internal_api_key: str = Field(default="", alias="CHATBOT_INTERNAL_API_KEY")

    # CSDL LibraryHub (read-only theo thiết kế read-tools)
    database_url: str = Field(default="", alias="DATABASE_URL")

    # Gemini
    gemini_api_key: str = Field(default="", alias="GEMINI_API_KEY")
    gemini_model: str = Field(default="gemini-2.0-flash", alias="GEMINI_MODEL")
    gemini_api_keys: str = Field(default="", alias="GEMINI_API_KEYS")

    # Giới hạn
    max_message_length: int = 2000
    request_timeout_seconds: int = 60
    enable_agentshield: bool = True
    max_history_messages: int = 12

    # Lưu trữ
    data_dir: str = "./data"
    audit_log_dir: str = "./data/audit"
    checkpoint_dir: str = "./data/checkpoints"
    export_dir: str = "./data/exports"
    session_dir: str = "./data/sessions"
    export_base_url: str = "/exports"

    def resolve_path(self, value: str) -> Path:
        path = Path(value)
        if not path.is_absolute():
            path = (BASE_DIR / path).resolve()
        return path

    @property
    def data_path(self) -> Path:
        return self.resolve_path(self.data_dir)

    @property
    def audit_path(self) -> Path:
        return self.resolve_path(self.audit_log_dir)

    @property
    def checkpoint_path(self) -> Path:
        return self.resolve_path(self.checkpoint_dir)

    @property
    def export_path(self) -> Path:
        return self.resolve_path(self.export_dir)

    @property
    def session_path(self) -> Path:
        return self.resolve_path(self.session_dir)

    def ensure_directories(self) -> None:
        for path in (
            self.data_path,
            self.audit_path,
            self.checkpoint_path,
            self.export_path,
            self.session_path,
        ):
            path.mkdir(parents=True, exist_ok=True)

    def gemini_credentials(self) -> list[dict[str, str]]:
        """Trả về danh sách credential Gemini theo thứ tự ưu tiên."""
        creds: list[dict[str, str]] = []
        if self.gemini_api_keys.strip():
            try:
                raw = json.loads(self.gemini_api_keys)
                if isinstance(raw, list):
                    for item in raw:
                        if not isinstance(item, dict):
                            continue
                        key = str(item.get("api_key") or "").strip()
                        model = str(item.get("model") or self.gemini_model).strip()
                        if key:
                            creds.append({"api_key": key, "model": model or self.gemini_model})
            except json.JSONDecodeError:
                # Bỏ qua cấu hình JSON sai để không làm sập service; sẽ fallback key đơn.
                pass
        if self.gemini_api_key.strip():
            creds.append({"api_key": self.gemini_api_key.strip(), "model": self.gemini_model.strip()})
        # Loại trùng theo (api_key, model)
        unique: list[dict[str, str]] = []
        seen: set[tuple[str, str]] = set()
        for c in creds:
            pair = (c["api_key"], c.get("model", ""))
            if pair in seen:
                continue
            seen.add(pair)
            unique.append(c)
        return unique


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    settings = Settings()
    settings.ensure_directories()
    return settings
