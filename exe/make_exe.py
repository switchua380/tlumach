#!/usr/bin/env python3
"""Make Tlumach.exe from the standard .NET apphost.

Tlumach.exe sits in the package root and the app lives in app\\, so the path to the
app DLL embedded in the apphost is changed from "TranslationLauncher.dll" to
"app\\TranslationLauncher.dll". No custom launcher code: the exe is Microsoft's own
apphost template (same as in every .NET app), which antivirus heuristics trust far
more than a small launcher that starts a hidden process.

Build:
  cd Launcher && dotnet build -c Release -r win-x64 -o out-win
  python3 ../exe/make_exe.py out-win/TranslationLauncher.exe <package>/Tlumach.exe
"""
import sys

src, dst = sys.argv[1], sys.argv[2]
data = bytearray(open(src, "rb").read())
old = b"TranslationLauncher.dll\0"
new = b"app\\TranslationLauncher.dll\0"
i = data.find(old)
assert i >= 0 and data.count(old) == 1, "embedded app path not found"
slot = data[i:i + 1024]
assert slot[len(old):].count(0) == 1024 - len(old), "unexpected apphost layout"
data[i:i + len(new)] = new
open(dst, "wb").write(data)
print("ok:", dst)
