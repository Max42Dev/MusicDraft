"""Spike-only: run ComfyUI with a hard PyTorch VRAM cap to approximate a smaller GPU.

python capped_launch.py <cap_gb> <path-to-ComfyUI-main.py> [ComfyUI args...]
Use with --disable-cuda-malloc (the native caching allocator honours the fraction) and a matching --reserve-vram.
This is an approximation: it does not model a slower/older card or driver differences.
"""
import os
import runpy
import sys

cap_gb = float(sys.argv[1])
main_py = sys.argv[2]
sys.argv = [main_py] + sys.argv[3:]
sys.path.insert(0, os.path.dirname(main_py))

import torch  # noqa: E402

total = torch.cuda.get_device_properties(0).total_memory
torch.cuda.set_per_process_memory_fraction(min(1.0, cap_gb * 1024 ** 3 / total), 0)
print(f"[capped_launch] PyTorch VRAM cap {cap_gb} GB of {total / 1024 ** 3:.1f} GB", flush=True)
runpy.run_path(main_py, run_name="__main__")
