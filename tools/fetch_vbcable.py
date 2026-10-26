"""Download the official VB-CABLE package and stage it for packaging.

The ZIP is pinned by SHA256; if VB-Audio publishes a new build the hash check
fails on purpose and this file must be updated after reviewing the new package.

Usage:  python tools/fetch_vbcable.py <destination-directory>
"""

from __future__ import annotations

import hashlib
import os
import sys
import urllib.request
import zipfile

URL = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip"
SHA256 = "B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB"


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__.strip())
        return 2
    destination = os.path.abspath(sys.argv[1])
    os.makedirs(destination, exist_ok=True)
    archive = os.path.join(destination, "VBCABLE_Driver_Pack45.zip")

    if not os.path.exists(archive):
        print(f"downloading {URL}")
        with urllib.request.urlopen(URL, timeout=120) as response:
            data = response.read()
        open(archive, "wb").write(data)

    digest = hashlib.sha256(open(archive, "rb").read()).hexdigest().upper()
    if digest != SHA256:
        print(f"SHA256 mismatch: {digest} != {SHA256}")
        os.remove(archive)
        return 1

    with zipfile.ZipFile(archive) as bundle:
        bundle.extractall(destination)
    setup = os.path.join(destination, "VBCABLE_Setup_x64.exe")
    if not os.path.exists(setup):
        print("VBCABLE_Setup_x64.exe missing after extraction")
        return 1
    print(f"VB-CABLE staged in {destination}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
