"""离线把 PP-OCR rec 模型动态量化为 int8。

这是**构建期一次性工具**，不参与运行时；运行时只用产出的 .onnx。
项目本身是 C#，但 ONNX Runtime 的量化器只随 Python 包发布，所以这里借 Python。

用法（建议用隔离 venv）::

    python -m venv .venv-quant
    .venv-quant/Scripts/pip install onnxruntime onnx
    .venv-quant/Scripts/python tools/quantize-ppocr.py \
        models/ch_PP-OCRv3_rec_infer.onnx models/ch_PP-OCRv3_rec_infer.int8.onnx

默认动态量化（权重量化、激活运行时量化），不需要校准数据集。

⚠️ 实测结论（2026-10-02）：对本项目的 **PP-OCRv3 rec（36 Conv + 32 BN）无效**。
`quantize_dynamic` 只量化 MatMul/Gemm，Conv 不动，模型 10.2→10.3MB，且单行 9ms→21ms（更慢）。
要用 int8 必须走**静态量化 + 校准集**（把 C# 预处理产出的输入张量喂给校准器），本工具暂不覆盖。
保留此脚本仅为记录与后续静态量化的起点。
"""

from __future__ import annotations

import argparse
import os
import sys

from onnxruntime.quantization import QuantType, quantize_dynamic


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Dynamic int8 quantization for PP-OCR rec model")
    parser.add_argument("input", help="fp32 输入模型 .onnx")
    parser.add_argument("output", help="int8 输出模型 .onnx")
    parser.add_argument(
        "--weight-type",
        default="qint8",
        choices=["qint8", "quint8"],
        help="权重类型，默认 qint8",
    )
    parser.add_argument(
        "--op-types",
        default=None,
        help="逗号分隔的待量化算子；缺省用 onnxruntime 默认集合",
    )
    args = parser.parse_args(argv)

    if not os.path.isfile(args.input):
        print(f"输入模型不存在：{args.input}", file=sys.stderr)
        return 2

    weight_type = QuantType.QInt8 if args.weight_type == "qint8" else QuantType.QUInt8
    op_types = args.op_types.split(",") if args.op_types else None

    quantize_dynamic(
        model_input=args.input,
        model_output=args.output,
        weight_type=weight_type,
        op_types_to_quantize=op_types,
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
