#!/usr/bin/python3
import os
import sys
args = sys.argv[1:]
for index, value in enumerate(args):
    if value == '--virtual-monitor':
        args[index + 1] = os.environ['SYNCBAR_QA_SIZE']
# Ubuntu's own shell mode brings its Yaru shell theme, which the default mode does not.
if os.environ.get('SYNCBAR_QA_MODE'):
    args.insert(1, '--mode=' + os.environ['SYNCBAR_QA_MODE'])
os.execv(args[0], args)
