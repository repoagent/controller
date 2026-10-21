"""Capture native Read-Host screen output in a disposable Windows console.

Run as a DETACHED_PROCESS worker, never against the runner's shared console.
Normal stdin/stdout/stderr still reach the actual public PowerShell process.
"""
import ctypes
from ctypes import wintypes
import json
import subprocess
import sys


class Coord(ctypes.Structure):
    _fields_ = [('x', ctypes.c_short), ('y', ctypes.c_short)]


class SmallRect(ctypes.Structure):
    _fields_ = [(name, ctypes.c_short) for name in ('left', 'top', 'right', 'bottom')]


class ScreenInfo(ctypes.Structure):
    _fields_ = [('size', Coord), ('cursor', Coord), ('attributes', wintypes.WORD),
                ('window', SmallRect), ('maximum', Coord)]


def main():
    request = json.load(sys.stdin)
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.AllocConsole.argtypes = []
    kernel.AllocConsole.restype = wintypes.BOOL
    kernel.FreeConsole.argtypes = []
    kernel.FreeConsole.restype = wintypes.BOOL
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                  ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.GetConsoleScreenBufferInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(ScreenInfo)]
    kernel.GetConsoleScreenBufferInfo.restype = wintypes.BOOL
    kernel.ReadConsoleOutputCharacterW.argtypes = [wintypes.HANDLE, wintypes.LPWSTR,
                                                  wintypes.DWORD, Coord, ctypes.POINTER(wintypes.DWORD)]
    kernel.ReadConsoleOutputCharacterW.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    if not kernel.AllocConsole():
        raise ctypes.WinError(ctypes.get_last_error())
    handle = None
    try:
        handle = kernel.CreateFileW('CONOUT$', 0x80000000, 3, None, 3, 0, None)
        if handle == wintypes.HANDLE(-1).value:
            raise ctypes.WinError(ctypes.get_last_error())
        result = subprocess.run(request['command'], input=request['answers'],
                                capture_output=True, text=True, encoding='utf-8', timeout=90)
        info = ScreenInfo()
        if not kernel.GetConsoleScreenBufferInfo(handle, ctypes.byref(info)):
            raise ctypes.WinError(ctypes.get_last_error())
        length = info.size.x * info.size.y
        screen = ctypes.create_unicode_buffer(length)
        read = wintypes.DWORD()
        if not kernel.ReadConsoleOutputCharacterW(handle, screen, length, Coord(0, 0), ctypes.byref(read)):
            raise ctypes.WinError(ctypes.get_last_error())
        text = screen[:read.value]
        lines = [text[start:start + info.size.x].rstrip() for start in range(0, len(text), info.size.x)]
        print(json.dumps({'returncode': result.returncode, 'stdout': result.stdout,
                          'stderr': result.stderr, 'console': '\n'.join(lines).rstrip()}))
    finally:
        if handle is not None and handle != wintypes.HANDLE(-1).value:
            kernel.CloseHandle(handle)
        kernel.FreeConsole()


if __name__ == '__main__':
    main()
