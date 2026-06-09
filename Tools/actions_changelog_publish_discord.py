#!/usr/bin/env python3

"""
Sends updates to a Discord webhook for new changelog entry from the DISCORD_CHANGELOG environment var
"""

import os
import json

import requests

DEBUG = False
DEBUG_CHANGELOG = '{"author": "TestAuthor","changes": [{"type": "Tweak", "message": "Test Tweak"},{"type": "Fix", "message": "Test Fix"}],"id": 123,"time": "2026-06-07T05:23:06.0000000+00:00"}'

# https://discord.com/developers/docs/resources/webhook
DISCORD_SPLIT_LIMIT = 2000
DISCORD_WEBHOOK_URL = os.environ.get("DISCORD_WEBHOOK_URL")

TYPES_TO_EMOJI = {"Fix": "🐛", "Add": "🆕", "Remove": "❌", "Tweak": "⚒️"}


def main():
    if not DISCORD_WEBHOOK_URL:
        print("No discord webhook URL found, skipping discord send")
        return

    if DEBUG:
        # to debug this script locally, we can use a test changelog
        rawChangelog = DEBUG_CHANGELOG
    else:
        # Pull the changelog from the env vars so we can pull it from the right step
        rawChangelog = os.environ.get("DISCORD_CHANGELOG", "")

    newChangelog = json.loads(rawChangelog)

    message_lines = changelog_to_message_lines(newChangelog)
    send_message_lines(message_lines)


def get_discord_body(content: str):
    return {
        "content": content,
        # Do not allow any mentions.
        "allowed_mentions": {"parse": []},
        # SUPPRESS_EMBEDS
        "flags": 1 << 2,
    }


def send_discord_webhook(lines: list[str]):
    content = "".join(lines)
    body = get_discord_body(content)

    response = requests.post(DISCORD_WEBHOOK_URL, json=body)
    response.raise_for_status()


def changelog_to_message_lines(newChangelog: dict) -> list[str]:
    """Process structured changelog entry into a list of lines making up a formatted message."""
    message_lines = []
    
    contributor_name = newChangelog.get("author", "N/A")

    message_lines.append(f"**{contributor_name}** updated:\n")

    url = newChangelog.get("url")
    if url and not url.strip():
        url = None

    for change in newChangelog.get("changes", []):
        emoji = TYPES_TO_EMOJI.get(change["type"], "❓")
        message = change.get("message", "N/A")

        # if a single line is longer than the limit, it needs to be truncated
        if len(message) > DISCORD_SPLIT_LIMIT:
            message = message[: DISCORD_SPLIT_LIMIT - 100].rstrip() + " [...]"

        if url is not None:
            line = f"{emoji} - {message} [PR]({url}) \n"
        else:
            line = f"{emoji} - {message}\n"

        message_lines.append(line)

    return message_lines


def send_message_lines(message_lines: list[str]):
    """Join a list of message lines into chunks that are each below Discord's message length limit, and send them."""
    chunk_lines = []
    chunk_length = 0

    for line in message_lines:
        line_length = len(line)
        new_chunk_length = chunk_length + line_length

        if new_chunk_length > DISCORD_SPLIT_LIMIT:
            print("Split changelog and sending to discord")
            send_discord_webhook(chunk_lines)

            new_chunk_length = line_length
            chunk_lines.clear()

        chunk_lines.append(line)
        chunk_length = new_chunk_length

    if chunk_lines:
        print("Sending changelog to discord")
        send_discord_webhook(chunk_lines)


if __name__ == "__main__":
    main()
