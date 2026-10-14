"""Prints the API log and Playwright failures as GitHub annotations (one each; at most 10 show per step)."""
import glob
import os


def emit(title: str, text: str) -> None:
    text = text[-12000:].replace("%", "%25").replace("\r", "").replace("\n", "%0A")
    print(f"::error title={title}::{text}")


if os.path.exists("api.log"):
    with open("api.log", errors="replace") as f:
        emit("api.log", "".join(f.readlines()[-120:]))

for path in sorted(glob.glob("portal/test-results/**/error-context.md", recursive=True))[:6]:
    with open(path, errors="replace") as f:
        emit("e2e " + os.path.basename(os.path.dirname(path))[:70], f.read()[:4000])
