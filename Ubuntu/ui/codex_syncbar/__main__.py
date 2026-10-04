import argparse
from .app import SyncBarApplication


def main():
    parser = argparse.ArgumentParser(description='Codex SyncBar Ubuntu 계정 관리자')
    parser.add_argument('--socket')
    parser.add_argument('--demo', action='store_true', help='별도 데모 서비스에 연결')
    parser.add_argument('--capture', help='이 앱 위젯만 PNG로 캡처하는 QA 모드')
    parser.add_argument('--width', type=int, default=1040)
    parser.add_argument('--height', type=int, default=760)
    parser.add_argument('--scroll-end', action='store_true', help='데모 QA에서 본문 하단 캡처')
    parser.add_argument('--light', action='store_true', help='QA 인스턴스의 밝은 테마')
    parser.add_argument('--profile-id', type=int, help='데모 QA 시작 계정')
    args = parser.parse_args()
    if (args.capture or args.profile_id is not None or args.scroll_end) and not args.demo:
        parser.error('QA 캡처와 action 실행에는 --demo가 필요합니다.')
    if args.demo and not args.socket:
        parser.error('--demo에는 운영 서비스와 분리된 --socket 경로가 필요합니다.')
    return SyncBarApplication(args).run([])


if __name__ == '__main__':
    raise SystemExit(main())
