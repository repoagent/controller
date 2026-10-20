"""Bounded-parent test helper for one freshly generated public Windows certificate.

No production caller, private key, child process, machine store or trust-policy edit.
Normal CurrentUser Root handles and ordinary production TLS remain required.
"""
import ctypes
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import sys

DWORD = ctypes.c_uint32
BOOL = ctypes.c_int32
POINTER = ctypes.c_void_p
WIDE = ctypes.c_wchar_p
CERT_STORE_PROV_SYSTEM_W = 10
CERT_SYSTEM_STORE_CURRENT_USER = 0x00010000
CERT_STORE_OPEN_EXISTING_FLAG = 0x00004000
X509_ASN_ENCODING = 0x00000001
PKCS_7_ASN_ENCODING = 0x00010000
CERT_FIND_SHA1_HASH = 0x00010000
CRYPTUI_WIZ_NO_UI = 0x00000001
CRYPTUI_WIZ_IMPORT_ALLOW_CERT = 0x00020000
CRYPTUI_WIZ_IMPORT_SUBJECT_CERT_CONTEXT = 2
CRYPT_E_NOT_FOUND = 0x80092004
INVALID_DATA = 13


class ImportInfo(ctypes.Structure):
    _fields_ = [('dwSize', DWORD), ('dwSubjectChoice', DWORD), ('pCertContext', POINTER),
                ('dwFlags', DWORD), ('pwszPassword', WIDE)]


class HashBlob(ctypes.Structure):
    _fields_ = [('cbData', DWORD), ('pbData', POINTER)]


class CertificateContext(ctypes.Structure):
    _fields_ = [('dwCertEncodingType', DWORD), ('pbCertEncoded', POINTER),
                ('cbCertEncoded', DWORD), ('pCertInfo', POINTER), ('hCertStore', POINTER)]


class FixtureFailure(Exception):
    pass


def record(operation, phase, status, error=0):
    # Only fixed phase/status labels and numeric errors, never certificate bytes,
    # command arguments, private keys or environment values.
    print(json.dumps({'operation': operation, 'phase': phase, 'status': status,
                      'win32': int(error) & 0xffffffff}), flush=True)


class WindowsStore:
    def __init__(self, operation, der, digest):
        self.operation, self.der = operation, der
        self.digest = ctypes.create_string_buffer(digest)
        self.blob = HashBlob(len(digest), ctypes.cast(self.digest, POINTER))
        crypt32 = ctypes.WinDLL('crypt32.dll', use_last_error=True)
        cryptui = ctypes.WinDLL('cryptui.dll', use_last_error=True)
        signatures = {
            'CertOpenStore': (crypt32, [POINTER, DWORD, POINTER, DWORD, POINTER], POINTER),
            'CertCreateCertificateContext': (crypt32, [DWORD, POINTER, DWORD], POINTER),
            'CertFindCertificateInStore': (crypt32, [POINTER, DWORD, DWORD, DWORD, POINTER, POINTER], POINTER),
            'CertDeleteCertificateFromStore': (crypt32, [POINTER], BOOL),
            'CertFreeCertificateContext': (crypt32, [POINTER], BOOL),
            'CertCloseStore': (crypt32, [POINTER, DWORD], BOOL),
            'CryptUIWizImport': (cryptui, [DWORD, POINTER, WIDE, POINTER, POINTER], BOOL),
        }
        self.functions = {}
        for name, (library, arguments, result) in signatures.items():
            function = getattr(library, name)
            function.argtypes, function.restype = arguments, result
            self.functions[name] = function

    def failure(self, phase, error):
        record(self.operation, phase, 'error', error)
        raise FixtureFailure()

    def invoke(self, phase, name, *arguments, absent_allowed=False):
        record(self.operation, phase, 'enter')
        ctypes.set_last_error(0)
        result = self.functions[name](*arguments)
        error = ctypes.get_last_error() & 0xffffffff if not result else 0
        if not result:
            if absent_allowed and error == CRYPT_E_NOT_FOUND:
                record(self.operation, phase, 'absent', error)
                return None
            self.failure(phase, error)
        record(self.operation, phase, 'ok')
        return result

    @contextmanager
    def opened(self, phase):
        name = ctypes.create_unicode_buffer('Root')
        store = self.invoke(phase, 'CertOpenStore', POINTER(CERT_STORE_PROV_SYSTEM_W), 0, None,
                            CERT_SYSTEM_STORE_CURRENT_USER | CERT_STORE_OPEN_EXISTING_FLAG,
                            ctypes.cast(name, POINTER))
        try:
            yield store
        finally:
            self.invoke('close', 'CertCloseStore', store, 0)

    def find(self, store, phase):
        found = self.invoke(phase, 'CertFindCertificateInStore', store, X509_ASN_ENCODING | PKCS_7_ASN_ENCODING,
                            0, CERT_FIND_SHA1_HASH, ctypes.byref(self.blob), None, absent_allowed=True)
        if found:
            try:
                context = ctypes.cast(found, ctypes.POINTER(CertificateContext)).contents
                if context.cbCertEncoded != len(self.der) or not context.pbCertEncoded:
                    self.failure('compare-der', INVALID_DATA)
                if ctypes.string_at(context.pbCertEncoded, context.cbCertEncoded) != self.der:
                    self.failure('compare-der', INVALID_DATA)
            except Exception:
                self.invoke('free', 'CertFreeCertificateContext', found)
                raise
        return found

    def add(self):
        with self.opened('open') as store:
            found = self.find(store, 'find-before')
            if found:
                self.invoke('free', 'CertFreeCertificateContext', found)
            else:
                encoded = ctypes.create_string_buffer(self.der)
                context = self.invoke('create', 'CertCreateCertificateContext', X509_ASN_ENCODING,
                                      ctypes.cast(encoded, POINTER), len(self.der))
                try:
                    info = ImportInfo(ctypes.sizeof(ImportInfo), CRYPTUI_WIZ_IMPORT_SUBJECT_CERT_CONTEXT, context, 0, '')
                    self.invoke('import', 'CryptUIWizImport', CRYPTUI_WIZ_NO_UI | CRYPTUI_WIZ_IMPORT_ALLOW_CERT, None, None,
                                ctypes.byref(info), store)
                finally:
                    self.invoke('free', 'CertFreeCertificateContext', context)
        # Verify persistence using a new NORMAL handle, never UNPROTECTED.
        with self.opened('reopen') as store:
            found = self.find(store, 'reopen-find')
            if not found:
                self.failure('verify-present', CRYPT_E_NOT_FOUND)
            self.invoke('free', 'CertFreeCertificateContext', found)

    def remove(self):
        with self.opened('open') as store:
            found = self.find(store, 'find-before')
            if found:
                # Delete consumes/frees this context on BOTH success and failure.
                # It must never be freed again or reused as a search cursor.
                consumed = found
                found = None
                self.invoke('delete', 'CertDeleteCertificateFromStore', consumed)
                remaining = self.find(store, 'find-after')
                if remaining:
                    self.invoke('free', 'CertFreeCertificateContext', remaining)
                    self.failure('verify-absent', 183)
            record(self.operation, 'absent', 'ok')


def main():
    operation = sys.argv[1] if len(sys.argv) == 4 and sys.argv[1] in ('add', 'remove') else 'invalid'
    try:
        if operation == 'invalid' or os.name != 'nt':
            record(operation, 'input', 'error', 50)
            return 1
        der = Path(sys.argv[2]).read_bytes()
        digest = bytes.fromhex(sys.argv[3])
        if not 0 < len(der) <= 65536 or len(digest) != 20 or hashlib.sha1(der).digest() != digest:
            record(operation, 'input', 'error', INVALID_DATA)
            return 1
        record(operation, 'load', 'enter')
        store = WindowsStore(operation, der, digest)
        record(operation, 'load', 'ok')
        getattr(store, operation)()
        record(operation, 'complete', 'ok')
        return 0
    except FixtureFailure:
        return 1
    except Exception as error:
        record(operation, 'python', 'error', getattr(error, 'winerror', None) or INVALID_DATA)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
