"""Telegram front-end: send links, watch the wardrobe pipeline run.

Deliberately thin. All it does is take links, run `pipeline.intake` in a worker
thread, and relay progress — the stages themselves live in their own modules
and are runnable from the CLI without any of this.

The bot stops where the pipeline stops: at the draft manifest. Naming a garment
and deciding how warm it is are judgement calls, and a `simId` is frozen the
moment art starts loading by it, so the bot hands over its evidence and waits.

Run it:  python -m wardrobe.bot
Token:   TELEGRAM_BOT_TOKEN in tools/wardrobe/.env (gitignored)
"""
from __future__ import annotations

import asyncio
import html
import logging
import os
import re
from pathlib import Path

from telegram import Update
from telegram.constants import ParseMode
from telegram.ext import (Application, CommandHandler, ContextTypes,
                          MessageHandler, filters)

from . import config, daz, pipeline, supervisor

log = logging.getLogger("wardrobe.bot")

_URL = re.compile(r"https?://\S+")
# One job at a time: DAZ Studio is a single shared instance, and two runs would
# fight over the same scene.
_lock = asyncio.Lock()


def load_env() -> None:
    env = Path(__file__).resolve().parent.parent / ".env"
    if not env.exists():
        return
    for line in env.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line and not line.startswith("#") and "=" in line:
            key, value = line.split("=", 1)
            os.environ.setdefault(key.strip(), value.strip())


def _allowed(update: Update) -> bool:
    """Only the configured chats may drive the machine.

    The bot can start DAZ jobs and write into the project, so an open bot is an
    open shell. With no allowlist configured the first chat to talk to it wins,
    which keeps setup easy without leaving it open to whoever finds the bot.
    """
    allowed = os.environ.get("TELEGRAM_ALLOWED_CHATS", "").strip()
    chat_id = str(update.effective_chat.id)
    if not allowed:
        os.environ["TELEGRAM_ALLOWED_CHATS"] = chat_id
        log.warning("привязался к чату %s (задайте TELEGRAM_ALLOWED_CHATS явно)", chat_id)
        return True
    return chat_id in {c.strip() for c in allowed.split(",")}


async def start(update: Update, _: ContextTypes.DEFAULT_TYPE) -> None:
    if not _allowed(update):
        return
    await update.message.reply_text(
        "Кидай ссылки на архивы с ассетами DAZ — по одной в строке.\n\n"
        "На каждый прогон я поднимаю отдельную сессию Claude Code. Она "
        "выполняет стадии и не лезет никуда, пока всё идёт хорошо; если "
        "что-то падает — разбирается и чинит скрипт сама. Её работу я "
        "пересылаю сюда по ходу дела.\n\n"
        "Останавливаемся на черновике манифеста: названия вещей и слоты — "
        "решение человека, а не вычисление.\n\n"
        "/stop — прервать прогон прямо сейчас\n"
        "/status — что сейчас происходит\n"
        "/raw <ссылки> — прогнать без надзирателя, голым конвейером")


async def status(update: Update, _: ContextTypes.DEFAULT_TYPE) -> None:
    if not _allowed(update):
        return
    await update.message.reply_text(
        ("Занят — идёт поставка." if _lock.locked() else "Свободен.")
        + f"\nНадзиратель: {'работает' if supervisor.is_running() else 'не запущен'}"
        + f"\nDAZ Studio: {'на связи' if daz.alive() else 'НЕ отвечает'}")


async def stop(update: Update, _: ContextTypes.DEFAULT_TYPE) -> None:
    """Kill the running session. The stages it already finished stay done."""
    if not _allowed(update):
        return
    if supervisor.cancel():
        await update.message.reply_text(
            "Остановил. Что успело отработать — осталось как есть; "
            "проверьте /status и отчёты перед повторным запуском.")
    else:
        await update.message.reply_text("Останавливать нечего — надзиратель не запущен.")


# Telegram throttles hard, and a working agent narrates faster than a chat can
# take. Lines are gathered for a moment and sent as one message rather than one
# each — otherwise the flood control drops exactly the lines worth reading.
_FLUSH_SECONDS = 4.0
_MAX_MESSAGE = 3500


def _relay(context: ContextTypes.DEFAULT_TYPE, chat: int):
    """A progress callback usable from a worker thread, plus its pump task."""
    queue: asyncio.Queue[str | None] = asyncio.Queue()
    loop = asyncio.get_running_loop()

    def progress(line: str) -> None:
        loop.call_soon_threadsafe(queue.put_nowait, line)

    async def send(text: str) -> None:
        for i in range(0, len(text), _MAX_MESSAGE):
            try:
                await context.bot.send_message(chat, text[i:i + _MAX_MESSAGE])
            except Exception:  # noqa: BLE001 — a lost line must not kill the job
                log.exception("не отправилась строка прогресса")

    async def pump() -> None:
        buffer: list[str] = []
        done = False
        while not done:
            try:
                line = await asyncio.wait_for(queue.get(), timeout=_FLUSH_SECONDS)
                if line is None:
                    done = True
                else:
                    buffer.append(line)
                    if sum(len(x) for x in buffer) < _MAX_MESSAGE:
                        continue
            except asyncio.TimeoutError:
                pass
            if buffer:
                await send("\n".join(buffer))
                buffer.clear()

    return progress, queue, pump


async def links(update: Update, context: ContextTypes.DEFAULT_TYPE) -> None:
    """Hand the job to a supervised Claude Code session."""
    if not _allowed(update):
        return
    urls = _URL.findall(update.message.text or "")
    if not urls:
        await update.message.reply_text("Не вижу ссылок.")
        return
    if _lock.locked():
        await update.message.reply_text("Уже занят поставкой — дождись конца.")
        return

    chat = update.effective_chat.id
    task = (
        "Провести новую поставку одежды по ссылкам:\n"
        + "\n".join(f"  {u}" for u in urls)
        + "\n\nПорядок: fetch → install → dress → build. Девушки — Genesis 3 "
          "Female, поэтому из каждого продукта нужны версии под G3F; если их "
          "нет, скажи об этом и не пытайся натянуть чужое поколение. "
          "Остановись на черновике манифеста — названия и слоты придумывает "
          "человек."
    )

    async with _lock:
        await update.message.reply_text(
            f"Принял {len(urls)} ссылк(и). Поднимаю сессию-надзиратель.")
        progress, queue, pump = _relay(context, chat)
        pumping = asyncio.create_task(pump())
        try:
            report = await asyncio.to_thread(supervisor.run, task, progress)
        except Exception as e:  # noqa: BLE001
            log.exception("надзиратель упал")
            report = {"ok": False, "errors": [f"неожиданная ошибка: {e}"]}
        finally:
            await queue.put(None)
            await pumping

    if report.get("ok"):
        await context.bot.send_message(chat, "✅ Поставка завершена.")
    else:
        problems = "\n".join(f"• {e}" for e in (report.get("errors") or ["без подробностей"]))
        await context.bot.send_message(chat, f"❌ Не доехали:\n{problems}")


async def raw(update: Update, context: ContextTypes.DEFAULT_TYPE) -> None:
    """The old fixed pipeline, with no agent watching — for comparison."""
    if not _allowed(update):
        return
    urls = _URL.findall(update.message.text or "")
    if not urls:
        await update.message.reply_text("Использование: /raw <ссылки>")
        return
    if _lock.locked():
        await update.message.reply_text("Уже занят поставкой — дождись конца.")
        return

    chat = update.effective_chat.id
    queue: asyncio.Queue[str | None] = asyncio.Queue()
    loop = asyncio.get_running_loop()

    def progress(line: str) -> None:
        loop.call_soon_threadsafe(queue.put_nowait, line)

    async def relay() -> None:
        while (line := await queue.get()) is not None:
            try:
                await context.bot.send_message(chat, line)
            except Exception:  # noqa: BLE001 — a lost progress line must not kill the job
                log.exception("не отправилась строка прогресса")

    async with _lock:
        await update.message.reply_text(f"Принял {len(urls)} ссылк(и). Начинаю.")
        relaying = asyncio.create_task(relay())
        try:
            report = await asyncio.to_thread(pipeline.intake, urls, progress)
        except Exception as e:  # noqa: BLE001
            log.exception("поставка упала")
            report = {"ok": False, "errors": [f"неожиданная ошибка: {e}"]}
        finally:
            await queue.put(None)
            await relaying

    if not report.get("ok"):
        problems = "\n".join(f"• {e}" for e in (report.get("errors") or ["без подробностей"]))
        await context.bot.send_message(chat, f"❌ Остановился:\n{problems}")
        return

    summary = pipeline.summarise(report["garments"])
    await context.bot.send_message(
        chat,
        f"✅ Поставка <b>{html.escape(report['drop'])}</b> собрана.\n"
        f"Манифест: <code>{html.escape(report['manifest'])}</code>\n\n"
        f"<pre>{html.escape(summary)}</pre>\n\n"
        "Дальше нужны названия, описания и параметры — скажи агенту, "
        "он заполнит манифест и прогонит Unity.",
        parse_mode=ParseMode.HTML)


def main() -> None:
    logging.basicConfig(level=logging.INFO,
                        format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    load_env()
    token = os.environ.get("TELEGRAM_BOT_TOKEN")
    if not token:
        raise SystemExit("нет TELEGRAM_BOT_TOKEN — положите его в tools/wardrobe/.env")

    config.DOWNLOADS.mkdir(parents=True, exist_ok=True)
    app = Application.builder().token(token).build()
    app.add_handler(CommandHandler("start", start))
    app.add_handler(CommandHandler("status", status))
    app.add_handler(CommandHandler("stop", stop))
    app.add_handler(CommandHandler("raw", raw))
    app.add_handler(MessageHandler(filters.TEXT & ~filters.COMMAND, links))
    log.info("бот запущен")
    app.run_polling()


if __name__ == "__main__":
    main()
