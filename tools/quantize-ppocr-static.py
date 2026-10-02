"""PP-OCR rec 模型的 **静态 int8 量化**（构建期工具，不参与运行时）。

背景
====
`tools/quantize-ppocr.py` 里的动态量化已被实测否决（该模型 36 个 Conv，
`quantize_dynamic` 只量化 MatMul，模型不变小还更慢）。要 int8 必须走
**静态量化 + 校准集**，本脚本就是那条路。

校准输入怎么来
==============
运行时喂给 rec 模型的是 C# 端 `PpOcrInput` 预处理后的张量。本脚本在 Python 里
**逐位复刻**同一套预处理，数据源是 `docs/ref/*.rows.json`（真机样本的行剖分结果）：

    图像 → 按 band(左/上/右/下) 裁剪 → 缩放到 48 高（半像素双线性）
         → 归一化 (v/255-0.5)/0.5 → 右侧补 0 到固定宽 → [1,3,48,W] float32

这样校准分布与真机一致，而不是拿随机噪声糊弄量化器。

用法
====
    .venv-quant/Scripts/python tools/quantize-ppocr-static.py \
        models/ch_PP-OCRv3_rec_infer.onnx \
        models/ch_PP-OCRv3_rec_infer.int8.onnx \
        --ref-dir docs/ref --fixed-width 640

产出只用于**离线对比**；确认精度/速度后再决定是否切换运行时默认模型。
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import sys

import numpy as np
from PIL import Image
from onnxruntime.quantization import (
    CalibrationDataReader,
    CalibrationMethod,
    QuantFormat,
    QuantType,
    quantize_static,
)

TARGET_HEIGHT = 48
NATURAL_MIN = 8
NATURAL_MAX = 1200


def natural_width(crop_w: int, crop_h: int) -> int:
    return max(NATURAL_MIN, min(NATURAL_MAX, int(np.ceil(TARGET_HEIGHT * crop_w / crop_h))))


def resize_halfpixel(src: np.ndarray, out_h: int, out_w: int) -> np.ndarray:
    """与 PpOcrInput.Fill 完全一致的半像素双线性缩放。

    src: (H, W, 3) 通道顺序 B,G,R。
    公式：s = (i + 0.5) * (in/out) - 0.5，向下取整后线性插值，边界钳制。
    """
    h, w = src.shape[:2]
    srcf = src.astype(np.float64)

    ys = (np.arange(out_h) + 0.5) * (h / out_h) - 0.5
    xs = (np.arange(out_w) + 0.5) * (w / out_w) - 0.5

    y0 = np.clip(np.floor(ys).astype(np.int64), 0, h - 1)
    y1 = np.minimum(y0 + 1, h - 1)
    fy = (ys - np.floor(ys))[:, None]

    x0 = np.clip(np.floor(xs).astype(np.int64), 0, w - 1)
    x1 = np.minimum(x0 + 1, w - 1)
    fx = (xs - np.floor(xs))[None, :]

    top = srcf[y0][:, x0] * (1 - fx)[..., None] + srcf[y0][:, x1] * fx[..., None]
    bottom = srcf[y1][:, x0] * (1 - fx)[..., None] + srcf[y1][:, x1] * fx[..., None]
    return top * (1 - fy)[..., None] + bottom * fy[..., None]


def preprocess(crop_bgr: np.ndarray, target_width: int) -> np.ndarray:
    """裁剪图 → [3,48,target_width] float32，与 PpOcrInput.Fill 对齐。"""
    h, w = crop_bgr.shape[:2]
    width = max(NATURAL_MIN, min(target_width, natural_width(w, h)))

    resized = resize_halfpixel(crop_bgr, TARGET_HEIGHT, width)

    planes = np.zeros((3, TARGET_HEIGHT, target_width), dtype=np.float32)
    norm = (resized / 255.0 - 0.5) / 0.5
    planes[:, :, :width] = np.transpose(norm, (2, 0, 1)).astype(np.float32)
    return planes


def find_image_for(rows_json: str) -> str | None:
    """由 *.rows.json 推出对应的源图。"""
    base = os.path.basename(rows_json)
    assert base.endswith(".rows.json")
    stem = base[: -len(".rows.json")]
    candidates: list[str] = []
    # panel_01.png.roi...rows.json 这种带 ROI 后缀的，取 .roi 之前的部分。
    if ".roi" in stem:
        prefix = stem.split(".roi")[0]
        candidates += [prefix + ".png", prefix + ".png.png"]
    candidates += [stem + ".png", stem + ".png.png"]
    d = os.path.dirname(rows_json)
    for c in candidates:
        p = os.path.join(d, c)
        if os.path.isfile(p):
            return p
    return None


def collect_calibration(ref_dir: str, fixed_width: int) -> list[np.ndarray]:
    rows_files = sorted(glob.glob(os.path.join(ref_dir, "*.rows.json")))
    tensors: list[np.ndarray] = []
    used: list[str] = []

    for rows_json in rows_files:
        image_path = find_image_for(rows_json)
        if image_path is None:
            print(f"  [跳过] 找不到源图：{os.path.basename(rows_json)}", file=sys.stderr)
            continue

        with open(rows_json, encoding="utf-8") as f:
            meta = json.load(f)
        region = meta.get("analyzedRegion")
        bands = meta.get("bands", [])
        if region is None or not bands:
            continue

        # PIL 读成 RGB，翻转通道得到 B,G,R —— 与 ImageFrame.Bgra 的前三通道一致。
        rgb = np.asarray(Image.open(image_path).convert("RGB"))
        bgr = np.ascontiguousarray(rgb[..., ::-1])

        rx, ry = int(region["x"]), int(region["y"])
        for band in bands:
            left = int(band["left"])
            right = int(band["right"])
            top = int(band["top"])
            bottom = int(band["bottom"])
            # rows.json 的 band 通常是**绝对像素**；仅当明显落在区域局部坐标里才补偏移。
            if rx > 0 and left < rx:
                left += rx
                right += rx
            if ry > 0 and top < ry:
                top += ry
                bottom += ry
            crop = bgr[top : bottom + 1, left : right + 1]
            if crop.size == 0:
                continue
            tensors.append(preprocess(crop, fixed_width)[None, ...])

        used.append(os.path.basename(image_path))
        print(f"  {os.path.basename(image_path)}: {len(bands)} 行")

    print(f"校准样本：{len(tensors)} 条，来自 {len(used)} 张图")
    return tensors


class TensorReader(CalibrationDataReader):
    def __init__(self, tensors: list[np.ndarray], input_name: str):
        self._tensors = tensors
        self._input_name = input_name
        self._iter = iter(tensors)

    def get_next(self):
        try:
            t = next(self._iter)
        except StopIteration:
            return None
        return {self._input_name: t}

    def rewind(self):
        self._iter = iter(self._tensors)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="PP-OCR rec 静态 int8 量化")
    parser.add_argument("input", help="fp32 输入模型 .onnx")
    parser.add_argument("output", help="int8 输出模型 .onnx")
    parser.add_argument("--ref-dir", default="docs/ref", help="校准样本目录（含 *.rows.json）")
    parser.add_argument("--fixed-width", type=int, default=640, help="校准输入宽度，与 DML 默认一致")
    parser.add_argument("--activation-type", default="quint8", choices=["quint8", "qint8"])
    parser.add_argument("--weight-type", default="qint8", choices=["qint8", "quint8"])
    parser.add_argument("--per-channel", action="store_true", default=True)
    parser.add_argument("--no-per-channel", dest="per_channel", action="store_false")
    parser.add_argument(
        "--calibrate-method",
        default="percentile",
        choices=["minmax", "entropy", "percentile"],
    )
    parser.add_argument(
        "--op-types",
        default=None,
        help="逗号分隔的待量化算子。逐元素算子(Mul/Add/Div)在 QDQ 融合后可能要求标量 scale 而加载失败，"
        "推荐只量化 Conv,MatMul。缺省用 onnxruntime 默认集合。",
    )
    parser.add_argument(
        "--preprocess",
        action="store_true",
        help="量化前先跑 onnxruntime 的 quant_pre_process（折叠 BatchNorm、符号形状推断）。"
        "该模型 Conv 后接独立 BatchNorm，不折叠会显著掉精度。",
    )
    args = parser.parse_args(argv)

    if not os.path.isfile(args.input):
        print(f"输入模型不存在：{args.input}", file=sys.stderr)
        return 2

    import onnxruntime as ort

    source_model = args.input
    if args.preprocess:
        from onnxruntime.quantization import quant_pre_process

        pre = args.output + ".pre.onnx"
        quant_pre_process(args.input, pre, skip_symbolic_shape=True)
        source_model = pre
        print(f"已预处理（BN 折叠/形状推断）：{pre}")

    input_name = ort.InferenceSession(
        source_model, providers=["CPUExecutionProvider"]
    ).get_inputs()[0].name
    print(f"模型输入名：{input_name}")

    print("收集校准数据：")
    tensors = collect_calibration(args.ref_dir, args.fixed_width)
    if not tensors:
        print("没有校准样本，终止。", file=sys.stderr)
        return 3

    act = QuantType.QUInt8 if args.activation_type == "quint8" else QuantType.QInt8
    weight = QuantType.QInt8 if args.weight_type == "qint8" else QuantType.QUInt8
    method = {
        "minmax": CalibrationMethod.MinMax,
        "entropy": CalibrationMethod.Entropy,
        "percentile": CalibrationMethod.Percentile,
    }[args.calibrate_method]

    quantize_static(
        model_input=source_model,
        model_output=args.output,
        calibration_data_reader=TensorReader(tensors, input_name),
        quant_format=QuantFormat.QDQ,
        per_channel=args.per_channel,
        activation_type=act,
        weight_type=weight,
        calibrate_method=method,
        op_types_to_quantize=args.op_types.split(",") if args.op_types else None,
        reduce_range=False,
    )

    src = os.path.getsize(args.input)
    dst = os.path.getsize(args.output)
    print(
        "已量化：{0} -> {1}  ({2:.1f} MB -> {3:.1f} MB, {4:.0%})".format(
            args.input, args.output, src / 1048576, dst / 1048576, dst / src
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
