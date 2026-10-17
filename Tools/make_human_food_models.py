"""Compatibility entry point: §56.7 source-derived anatomy, no primitive fallback."""
import runpy
from pathlib import Path
runpy.run_path(str(Path(__file__).parent / "Art/slice_human_parts.py"), run_name="__main__")
