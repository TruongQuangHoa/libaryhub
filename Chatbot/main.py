"""Điểm chạy dịch vụ Chatbot Python cho LibraryHub.

Chạy phát triển:
    python -m uvicorn app.main:app --host 127.0.0.1 --port 8010 --reload

Hoặc:
    python main.py
"""

from __future__ import annotations

import uvicorn

from app.config import get_settings


def main() -> None:
    settings = get_settings()
    uvicorn.run(
        "app.main:app",
        host=settings.host,
        port=settings.port,
        reload=settings.app_env.lower() == "development",
    )


if __name__ == "__main__":
    main()
