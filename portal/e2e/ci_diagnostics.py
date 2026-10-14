"""Prints the API log and Playwright failures as GitHub annotations (one each; at most 10 show per step)."""
import glob
import os
import re


def emit(title: str, text: str) -> None:
    text = text[-12000:].replace("%", "%25").replace("\r", "").replace("\n", "%0A")
    print(f"::error title={title}::{text}")


def section(text: str, name: str) -> str:
    match = re.search(rf"^# {name}\n(.*?)(?=^# |\Z)", text, re.S | re.M)
    return match.group(1).strip() if match else ""


if os.path.exists("api.log"):
    with open("api.log", errors="replace") as f:
        emit("api.log", "".join(f.readlines()[-120:]))

for path in sorted(glob.glob("portal/test-results/**/error-context.md", recursive=True))[:6]:
    with open(path, errors="replace") as f:
        text = f.read()
    # The failing line is marked with ">" in the test source; keep it with a few lines around it.
    source = section(text, "Test source").splitlines()
    marked = next((i for i, line in enumerate(source) if line.lstrip().startswith(">")), len(source))
    around = "\n".join(source[max(0, marked - 4): marked + 3])
    snapshot = section(text, "Page snapshot")
    body = f"{section(text, 'Error details')}\n--- at\n{around}\n--- page (end)\n{snapshot[-3500:]}"
    emit("e2e " + os.path.basename(os.path.dirname(path))[:70], body)
