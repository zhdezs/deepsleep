# -*- coding: utf-8 -*-
"""科学计算器：安全地算数学表达式。

调用方式（deepsleep 会自动传）：
    python main.py "sin(pi/2)+2**10"
也支持环境变量 DEEPSLEEP_TOOL_INPUT。
"""
import ast, math, os, sys

ALLOWED_FUNCS = {
    "sin": math.sin, "cos": math.cos, "tan": math.tan,
    "asin": math.asin, "acos": math.acos, "atan": math.atan,
    "sqrt": math.sqrt, "log": math.log, "log10": math.log10, "log2": math.log2,
    "exp": math.exp, "abs": abs, "round": round, "floor": math.floor, "ceil": math.ceil,
    "factorial": math.factorial, "gcd": math.gcd, "hypot": math.hypot,
}
CONSTS = {"pi": math.pi, "e": math.e, "tau": math.tau}

# 只放行白名单里的节点，避免 eval 变成"执行任意代码"的后门
NODES = (ast.Expression, ast.BinOp, ast.UnaryOp, ast.Call, ast.Name, ast.Load,
         ast.Constant, ast.Add, ast.Sub, ast.Mult, ast.Div, ast.Pow, ast.Mod,
         ast.FloorDiv, ast.USub, ast.UAdd, ast.Tuple, ast.keyword)


def evaluate(expr):
    tree = ast.parse(expr, mode="eval")
    for node in ast.walk(tree):
        if not isinstance(node, NODES):
            raise ValueError("表达式里有不支持的写法：%s" % type(node).__name__)
        if isinstance(node, ast.Call) and not (
                isinstance(node.func, ast.Name) and node.func.id in ALLOWED_FUNCS):
            raise ValueError("不认识的函数：%s" % getattr(node.func, "id", "?"))
        if isinstance(node, ast.Name) and not (
                node.id in ALLOWED_FUNCS or node.id in CONSTS):
            raise ValueError("不认识的变量：%s" % node.id)
    return eval(compile(tree, "<calc>", "eval"), {"__builtins__": {}}, {**CONSTS, **ALLOWED_FUNCS})


def fmt(v):
    if isinstance(v, float):
        if v != v or v in (float("inf"), float("-inf")):
            return str(v)
        if abs(v - round(v)) < 1e-12 and abs(v) < 1e15:
            return str(int(round(v)))
        return ("%.12g" % v)
    return str(v)


def main():
    expr = " ".join(sys.argv[1:]).strip() or os.environ.get("DEEPSLEEP_TOOL_INPUT", "").strip()
    if not expr:
        print("用法：科学计算器 输入=<数学表达式>，例如 sin(pi/2)+2**10")
        return 2
    # 中文里常见的符号统一一下
    expr = (expr.replace("×", "*").replace("÷", "/").replace("−", "-")
                .replace("（", "(").replace("）", ")").replace("，", ",").replace("^", "**"))
    try:
        val = evaluate(expr)
    except ZeroDivisionError:
        print("除零了，算不出来。")
        return 1
    except Exception as e:
        print("算不了这个式子：%s\n表达式：%s" % (e, expr))
        return 1
    print("%s = %s" % (expr, fmt(val)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
