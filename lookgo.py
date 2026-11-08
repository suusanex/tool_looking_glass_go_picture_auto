#!/usr/bin/env python3
# /// script
# requires-python = ">=3.12,<3.13"
# dependencies = [
#   "filelock==3.20.0",
#   "numpy==2.2.6",
#   "pillow==12.0.0",
#   "transformers==4.57.1",
#   "safetensors==0.6.2",
#   "torch==2.8.0",
#   "pytest==8.4.2",
# ]
# [tool.uv]
# [[tool.uv.index]]
# name = "pytorch-cu128"
# url = "https://download.pytorch.org/whl/cu128"
# explicit = true
# [tool.uv.sources]
# torch = { index = "pytorch-cu128" }
# ///

from lookgo.cli import main


if __name__ == "__main__":
    raise SystemExit(main())
