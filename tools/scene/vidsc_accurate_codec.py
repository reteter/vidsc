"""Production codec primitives adapted from the accepted finite M4a reference."""
from fractions import Fraction
import hashlib
import numpy as np

def require(value, message):
    if not value:
        raise ValueError(message)
def digest(data):
    return hashlib.sha256(data).hexdigest()
def configuration(extra, codec):
    """Return unchanged NALs from avcC/hvcC, with explicit length-size checks."""
    require(extra and extra[0] == 1, "Configuration is not avcC/hvcC")
    units = []
    def take(offset, count):
        for _ in range(count):
            size = int.from_bytes(extra[offset:offset + 2], "big")
            offset += 2
            require(size > 0 and offset + size <= len(extra), "Truncated parameter set")
            units.append(extra[offset:offset + size])
            offset += size
        return offset
    if codec == "h264":
        require(extra[4] & 3 == 3, "Only four-byte NAL lengths are supported")
        offset = take(6, extra[5] & 31)
        take(offset + 1, extra[offset])
    else:
        require(extra[21] & 3 == 3, "Only four-byte NAL lengths are supported")
        offset = 23
        for _ in range(extra[22]):
            count = int.from_bytes(extra[offset + 1:offset + 3], "big")
            offset = take(offset + 3, count)
    return units
def nals(data):
    result, offset = [], 0
    while offset < len(data):
        size = int.from_bytes(data[offset:offset + 4], "big")
        offset += 4
        require(size > 0 and offset + size <= len(data), "Invalid length-prefixed NAL")
        result.append(data[offset:offset + size])
        offset += size
    return result
def pack(units):
    return b"".join(len(unit).to_bytes(4, "big") + unit for unit in units)
def payload(data, codec):
    return digest(pack([unit for unit in nals(data)
        if (unit[0] & 31 in (1, 5) if codec == "h264" else (unit[0] >> 1) & 63 <= 31)]))
def quality(left, right, codec):
    a, b = left.astype(np.float64), right.astype(np.float64)
    peak = 255 if codec == "h264" else 1023
    mse = np.mean((a - b) ** 2)
    psnr = float("inf") if mse == 0 else float(10 * np.log10(peak * peak / mse))
    # Fixed 8x8 non-overlapping block SSIM over the native planar array.
    rows, columns = a.shape[0] // 8 * 8, a.shape[1] // 8 * 8
    a = a[:rows, :columns].reshape(-1, 8, columns // 8, 8).transpose(0, 2, 1, 3).reshape(-1, 64)
    b = b[:rows, :columns].reshape(-1, 8, columns // 8, 8).transpose(0, 2, 1, 3).reshape(-1, 64)
    ma, mb = a.mean(1), b.mean(1)
    va, vb = a.var(1), b.var(1)
    cov = ((a - ma[:, None]) * (b - mb[:, None])).mean(1)
    c1, c2 = (0.01 * peak) ** 2, (0.03 * peak) ** 2
    ssim = ((2 * ma * mb + c1) * (2 * cov + c2) /
        ((ma ** 2 + mb ** 2 + c1) * (va + vb + c2))).mean()
    return psnr, float(ssim)
def boundary_identity_v4(source, output, expected):
    if output["hash"] == source[expected]["hash"]:
        return "exact-native-at-expected-pts"
    fingerprints = np.stack([frame["fingerprint"] for frame in source])
    distances = np.mean((fingerprints - output["fingerprint"]) ** 2, axis=(1, 2))
    order = np.argsort(distances)
    require(order[0] == expected and distances[order[1]] > distances[expected] * 1.02 + .25,
        "Lossy boundary fingerprint is ambiguous or wrong")
    return "unique-lossy"
