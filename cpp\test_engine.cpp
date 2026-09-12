// -*- coding: utf-8 -*-
#include <iostream>
#include <fstream>
#include "engine.h"

int main() {
    std::ofstream log("test_log.txt");
    log << "start" << std::endl;

    log << "classify1" << std::endl;
    auto c1 = engine::classify("你懂个屁，键盘侠");
    log << "c1=" << c1 << std::endl;

    log << "classify2" << std::endl;
    auto c2 = engine::classify("等着瞧，我举报你");
    log << "c2=" << c2 << std::endl;

    log << "mask" << std::endl;
    auto m = engine::mask_profanity("你这个傻逼废物");
    log << "m=" << m << std::endl;

    log << "quotes" << std::endl;
    auto q = engine::extract_quotes("你懂个屁，键盘侠，就你这种人也配说话");
    for (auto& x : q) log << "[" << x << "] ";
    log << std::endl;

    log << "engine" << std::endl;
    engine::Engine e;
    e.new_session(1);
    auto r = e.generate("你懂个屁，键盘侠，就你这种人也配说话", 1);
    log << "turns=" << r.turns << " cat=" << r.category_name << " agg=" << r.aggression << std::endl;
    log << r.response << std::endl;

    log << "done" << std::endl;
    return 0;
}
