#!/usr/bin/python3
import os
import sys
args = sys.argv[1:]
for index, value in enumerate(args):
    if value == '--virtual-monitor':
        args[index + 1] = os.environ['SYNCBAR_QA_SIZE']
os.execv(args[0], args)
