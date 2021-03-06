﻿using System.Threading;
using System.Collections.Generic;
using MyShogi.App;
using MyShogi.Model.Shogi.Core;
using MyShogi.Model.Shogi.Player;
using MyShogi.Model.Shogi.Kifu;
using MyShogi.Model.Resource.Sounds;
using MyShogi.Model.Shogi.Usi;

namespace MyShogi.Model.Shogi.LocalServer
{
    public partial class LocalGameServer
    {

        #region 対局監視スレッド

        /// <summary>
        /// スレッドによって実行されていて、対局を管理している。
        /// pooling用のthread。少しダサいが、通知によるコールバックモデルでやるよりコードがシンプルになる。
        /// どのみち持ち時間の監視などを行わないといけないので、このようなworker threadは必要だと思う。
        /// </summary>
        private void thread_worker()
        {
            while (!workerStop)
            {
                // 各プレイヤーのプロセスの標準入出力に対する送受信の処理
                foreach (var player in Players)
                {
                    player.OnIdle();
                }

                // UI側からのコマンドがあるかどうか。あればdispatchする。
                CheckUICommand();

                // 各プレイヤーから指し手が指されたかのチェック
                CheckPlayerMove();

                // 時間消費のチェック。時間切れのチェック。
                CheckTime();

                // 10msごとに各種処理を行う。
                Thread.Sleep(10);
            }
        }

        /// <summary>
        /// UI側からのコマンドがあるかどうかを調べて、あれば実行する。
        /// </summary>
        private void CheckUICommand()
        {
            List<UICommand> commands = null;
            lock (UICommandLock)
            {
                if (UICommands.Count != 0)
                {
                    // コピーしてからList.Clear()を呼ぶぐらいなら参照をすげ替えて、newしたほうが速い。
                    commands = UICommands;
                    UICommands = new List<UICommand>();
                }
            }
            // lockの外側で呼び出さないとdead lockになる。
            if (commands != null)
                foreach (var command in commands)
                    command();
        }

        /// <summary>
        /// 「先手」「後手」の読み上げは、ゲーム開始後、初回のみなので
        /// そのためのフラグ
        /// </summary>
        private bool[] sengo_read_out;

        /// <summary>
        /// 対局開始のためにGameSettingの設定に従い、ゲームを初期化する。
        /// </summary>
        /// <param name="gameSetting"></param>
        private void GameStart(GameSetting gameSetting)
        {
            // 以下の初期化中に駒が動かされるの気持ち悪いのでユーザー操作を禁止しておく。
            CanUserMove = false;
            lastInitializing = true;

            // 音声:「よろしくお願いします。」
            TheApp.app.soundManager.Stop(); // 再生中の読み上げをすべて停止
            TheApp.app.soundManager.ReadOut(SoundEnum.Start);

            // 初回の指し手で、「先手」「後手」と読み上げるためのフラグ
            sengo_read_out = new bool[2] { false, false };

            // プレイヤーの生成
            foreach (var c in All.Colors())
            {
                var playerType = gameSetting.Player(c).IsHuman ? PlayerTypeEnum.Human : PlayerTypeEnum.UsiEngine;
                Players[(int)c] = PlayerBuilder.Create(playerType);
            }

            // 局面の設定
            kifuManager.EnableKifuList = true;
            if (gameSetting.Board.BoardTypeCurrent)
            {
                // 現在の局面からなので、いま以降の局面を削除する。
                // ただし、いまの局面と棋譜ウィンドウとが同期しているとは限らない。
                // まず現在局面以降の棋譜を削除しなくてはならない。

                // 元nodeが、special moveであるなら、それを削除しておく。
                if (kifuManager.Tree.IsSpecialNode())
                    kifuManager.Tree.UndoMove();

                kifuManager.Tree.ClearForward();

                // 分岐棋譜かも知れないので、現在のものを本譜の手順にする。
                kifuManager.Tree.MakeCurrentNodeMainBranch();
            }
            else // if (gameSetting.Board.BoardTypeEnable)
            {
                kifuManager.Init();
                kifuManager.InitBoard(gameSetting.Board.BoardType);
            }

            // 本譜の手順に変更したので現在局面と棋譜ウィンドウのカーソルとを同期させておく。
            UpdateKifuSelectedIndex();

            // 現在の時間設定を、KifuManager.Treeに反映させておく(棋譜保存時にこれが書き出される)
            kifuManager.Tree.KifuTimeSettings = gameSetting.KifuTimeSettings;

            // 対局者氏名の設定
            // 人間の時のみ有効。エンジンの時は、エンジン設定などから取得することにする。(TODO:あとで考える)
            foreach (var c in All.Colors())
            {
                var player = Player(c);
                string name;
                switch (player.PlayerType)
                {
                    case PlayerTypeEnum.Human:
                        name = gameSetting.Player(c).PlayerName;
                        break;

                    default:
                        name = c.Pretty();
                        break;
                }

                kifuManager.KifuHeader.SetPlayerName(c, name);
            }

            // 持ち時間などの設定が必要なので、コピーしておく。
            GameSetting = gameSetting;

            // 消費時間計算用
            foreach (var c in All.Colors())
            {
                var pc = PlayTimer(c);
                pc.KifuTimeSetting = GameSetting.KifuTimeSettings.Player(c);
                pc.GameStart();
            }

            // rootの持ち時間設定をここに反映させておかないと待ったでrootまで持ち時間が戻せない。
            // 途中の局面からだとここではなく、現局面のところに書き出す必要がある。
            kifuManager.Tree.SetKifuMoveTimes(PlayTimers.GetKifuMoveTimes());

            // コンピュータ vs 人間である場合、人間側を手前にしてやる。
            // 人間 vs 人間の場合も最初の手番側を手前にしてやる。
            var stm = kifuManager.Position.sideToMove;
            // 1. 手番側が人間である場合(非手番側が人間 or CPU)
            if (gameSetting.Player(stm).IsHuman)
                BoardReverse = (stm == Color.WHITE);
            // 2. 手番側がCPUで、非手番側が人間である場合。
            else if (gameSetting.Player(stm).IsCpu && gameSetting.Player(stm.Not()).IsHuman)
                BoardReverse = (stm == Color.BLACK);

            // プレイヤー情報などを検討ダイアログに反映させる。
            var nextGameMode = GameModeEnum.InTheGame;
            InitEngineConsiderationInfo(nextGameMode);

            // 検討モードならそれを停止させる必要があるが、それはGameModeのsetterがやってくれる。
            GameMode = nextGameMode;
        }

        /// <summary>
        /// プレイヤー情報を検討ダイアログに反映させる。
        /// </summary>
        private void InitEngineConsiderationInfo(GameModeEnum nextGameMode)
        {
            // CPUの数をNumberOfEngineに反映。
            int num = 0;
            foreach (var c in All.Colors())
                if (GameSetting.Player(c).IsCpu)
                    ++num;
            NumberOfEngine = num;

            // エンジン数が確定したので、検討ウィンドウにNumberOfInstanceメッセージを送信してやる。
            ThinkReport = new UsiThinkReportMessage()
            {
                type = UsiEngineReportMessageType.NumberOfInstance,
                number = NumberOfEngine,
            };
            ThinkReport = new UsiThinkReportMessage()
            {
                type = UsiEngineReportMessageType.SetGameMode,
                data = nextGameMode
            };

            // 各エンジンの情報を検討ウィンドウにリダイレクトするようにハンドラを設定
            num = 0;
            foreach (var c in All.Colors())
            {
                if (GameSetting.Player(c).IsCpu)
                {
                    var num_ = num; // copy for capturing

                    // 検討モードなら、名前は..
                    var name =
                        (nextGameMode == GameModeEnum.ConsiderationWithEngine    ) ? "検討用エンジン" :
                        (nextGameMode == GameModeEnum.ConsiderationWithMateEngine) ? "詰将棋エンジン" :
                            DisplayName(c);

                    ThinkReport = new UsiThinkReportMessage()
                    {
                        type = UsiEngineReportMessageType.SetEngineName,
                        number = num_, // is captured
                        data = name,
                    };

                    // UsiEngineのThinkReportプロパティを捕捉して、それを転送してやるためのハンドラをセットしておく。
                    var engine_player = Player(c) as UsiEnginePlayer;
                    engine_player.engine.AddPropertyChangedHandler("ThinkReport", (args) =>
                    {
                        //// 1) 読み筋の抑制条件その1
                        //// 人間対CPUで、メニューの「ウィンドウ」のところで表示するになっていない場合。
                        //var surpress1 = NumberOfEngine == 1 && !TheApp.app.config.EngineConsiderationWindowEnableWhenVsHuman;

                        if (ThinkReportEnable
                            /* && !(surpress1) */ )
                        {
                            var report = args.value as UsiThinkReport;

                            // このクラスのpropertyのsetterを呼び出してメッセージを移譲してやる。
                            ThinkReport = new UsiThinkReportMessage()
                            {
                                type = UsiEngineReportMessageType.UsiThinkReport,
                                number = num_, // is captrued
                                data = report,
                            };
                        }
                    });

                    num++;
                }
            }
        }

        /// <summary>
        /// このLocalGameServerのインスタンスの管理下で現在動作しているエンジンの数 (0～2)
        /// これが0のときは人間同士の対局などなので、検討ウィンドウを表示しない。
        /// これが1のときは、1つしかないので、EngineConsiderationDialogには、そいつの出力を0番のインスタンスとして読み筋を出力。
        /// これが2のときは、EngineConsiderationDialogに、先手を0番、後手を1番として、読み筋を出力。
        /// </summary>
        private int NumberOfEngine;

        /// <summary>
        /// 指し手が指されたかのチェックを行う
        /// </summary>
        private void CheckPlayerMove()
        {
            // 現状の局面の手番側
            var stm = Position.sideToMove;
            var stmPlayer = Player(stm);

            var config = TheApp.app.config;

            // -- 指し手

            Move bestMove;
            if (GameMode.IsWithEngine())
            {
                // 検討モードなのでエンジンから送られてきたbestMoveの指し手は無視。
                bestMove = stmPlayer.SpecialMove;
            }
            else
            {
                // TIME_UPなどのSpecialMoveが積まれているなら、そちらを優先して解釈する。
                bestMove = stmPlayer.SpecialMove != Move.NONE ? stmPlayer.SpecialMove : stmPlayer.BestMove;
            }

            if (bestMove != Move.NONE)
            {
                PlayTimer(stm).ChageToThemTurn(bestMove == Move.TIME_UP);

                stmPlayer.SpecialMove = Move.NONE; // クリア

                // 駒が動かせる状況でかつ合法手であるなら、受理する。

                bool specialMove = false;
                if (GameMode == GameModeEnum.InTheGame)
                {
                    // 送信されうる特別な指し手であるか？
                    specialMove = bestMove.IsSpecial();

                    // エンジンから送られてきた文字列が、illigal moveであるならエラーとして表示する必要がある。

                    if (specialMove)
                    {
                        switch (bestMove)
                        {
                            // 入玉宣言勝ち
                            case Move.WIN:
                                if (Position.DeclarationWin(EnteringKingRule.POINT27) != Move.WIN)
                                    // 入玉宣言条件を満たしていない入玉宣言
                                    goto ILLEGAL_MOVE;
                                break;

                            // 中断
                            // コンピューター同士の対局の時にも人間判断で中断できなければならないので
                            // 対局中であればこれを無条件で受理する。
                            case Move.INTERRUPT:
                            // 時間切れ
                            // 時間切れになるとBestMoveに自動的にTIME_UPが積まれる。これも無条件で受理する。
                            case Move.TIME_UP:
                                break;

                            // 投了
                            case Move.RESIGN:
                                break; // 手番側の投了は無条件で受理

                            // それ以外
                            default:
                                // それ以外は受理しない
                                goto ILLEGAL_MOVE;
                        }
                    }
                    else if (!Position.IsLegal(bestMove))
                        // 合法手ではない
                        goto ILLEGAL_MOVE;


                    // -- bestMoveを受理して、局面を更新する。

                    kifuManager.Tree.AddNode(bestMove, PlayTimers.GetKifuMoveTimes());

                    // 受理できる性質の指し手であることは検証済み
                    // special moveであってもDoMove()してしまう。
                    kifuManager.DoMove(bestMove);

                    // -- 音声の読み上げ

                    var soundManager = TheApp.app.soundManager;

                    var kif = kifuManager.KifuList[kifuManager.KifuList.Count - 1];
                    // special moveはMoveを直接渡して再生。
                    if (bestMove.IsSpecial())
                        soundManager.ReadOut(bestMove);
                    else
                    {
                        // -- 駒音

                        if (TheApp.app.config.PieceSoundInTheGame != 0)
                        {

                            // 移動先の升の下に別の駒があるときは、駒がぶつかる音になる。
                            var to = bestMove.To();
                            var delta = stm == Color.BLACK ? Square.SQ_D : Square.SQ_U;
                            var to2 = to + (int)delta;
                            // to2が盤外であることがあるので、IsOk()を通すこと。
                            var e = (to2.IsOk() && Position.PieceOn(to2) != Piece.NO_PIECE)
                                ? SoundEnum.KOMA_B1 /*ぶつかる音*/: SoundEnum.KOMA_S1 /*ぶつからない音*/;

#if false
                            // あまりいい効果音作れなかったのでコメントアウトしとく。
                            if (TheApp.app.config.CrashPieceSoundInTheGame != 0)
                            {
                                // ただし、captureか捕獲する指し手であるなら、衝撃音に変更する。
                                if (Position.State().capturedPiece != Piece.NO_PIECE || Position.InCheck())
                                    e = SoundEnum.KOMA_C1;
                            }
#endif
                            soundManager.PlayPieceSound(e);
                        }

                        // -- 棋譜の読み上げ

                        // 「先手」と「後手」と読み上げる。
                        if (!sengo_read_out[(int)stm] || config.ReadOutSenteGoteEverytime != 0)
                        {
                            sengo_read_out[(int)stm] = true;

                            // 駒落ちの時は、「上手(うわて)」と「下手(したて)」
                            if (!Position.Handicapped)
                                soundManager.ReadOut(stm == Color.BLACK ? SoundEnum.Sente : SoundEnum.Gote);
                            else
                                soundManager.ReadOut(stm == Color.BLACK ? SoundEnum.Shitate : SoundEnum.Uwate);
                        }

                        // 棋譜文字列をそのまま頑張って読み上げる。
                        soundManager.ReadOut(kif);
                    }

                }

                // -- 次のPlayerに、自分のturnであることを通知してやる。

                if (!specialMove)
                    NotifyTurnChanged();
                else
                    // 特殊な指し手だったので、これにてゲーム終了
                    GameEnd();
            }

            return;

        ILLEGAL_MOVE:

            // これ、棋譜に記録すべき
            Move m = Move.ILLEGAL_MOVE;
            kifuManager.Tree.AddNode(m, PlayTimers.GetKifuMoveTimes());
            kifuManager.Tree.AddNodeComment(m, stmPlayer.BestMove.ToUsi() /* String あとでなおす*/ /* 元のテキスト */);
            kifuManager.Tree.DoMove(m);

            GameEnd(); // これにてゲーム終了。
        }

        /// <summary>
        /// 手番側のプレイヤーに自分の手番であることを通知するためにThink()を呼び出す。
        /// また、手番側のCanMove = trueにする。非手番側のプレイヤーに対してCanMove = falseにする。
        /// </summary>
        private void NotifyTurnChanged()
        {
            var stm = Position.sideToMove;

            // 検討モードでは、先手側のプレイヤーがエンジンに紐づけられている。
            if (GameMode.IsWithEngine())
                stm = Color.BLACK;

            var stmPlayer = Player(stm);
            var isHuman = stmPlayer.PlayerType == PlayerTypeEnum.Human;

            // 手番が変わった時に特殊な局面に至っていないかのチェック
            if (GameMode == GameModeEnum.InTheGame)
            {
                var misc = TheApp.app.config.GameSetting.MiscSettings;
                Move m = kifuManager.Tree.IsNextNodeSpecialNode(isHuman , misc);

                // 上で判定された特殊な指し手であるか？
                if (m != Move.NONE)
                {
                    // この特殊な状況を棋譜に書き出して終了。
                    kifuManager.Tree.AddNode(m, KifuMoveTimes.Zero);
                    // speical moveでもDoMoveできることは保証されている。
                    kifuManager.Tree.DoMove(m);

                    // 音声の読み上げ
                    TheApp.app.soundManager.ReadOut(m);

                    GameEnd();
                    return;
                }
            }

            // USIエンジンのときだけ、"position"コマンドに渡す形で局面図が必要であるから、
            // 生成して、それをPlayer.Think()の引数として渡してやる。
            var isUsiEngine = stmPlayer.PlayerType == PlayerTypeEnum.UsiEngine;
            string usiPosition = isUsiEngine ? kifuManager.UsiPositionString : null;

            stmPlayer.CanMove = true;
            stmPlayer.SpecialMove = Move.NONE;

            // BestMove,PonderMoveは、Think()以降、正常に更新されることは、Playerクラス側で保証されているので、
            // ここではそれらの初期化は行わない。

            // -- MultiPVの設定

            if (GameMode == GameModeEnum.ConsiderationWithEngine)
                // MultiPVは、GlobalConfigの設定を引き継ぐ
                (stmPlayer as UsiEnginePlayer).engine.MultiPV = TheApp.app.config.ConsiderationMultiPV;
                // それ以外のGameModeなら、USIのoption設定を引き継ぐので変更しない。


            // -- Think()

            // エンジン検討モードなら時間無制限
            // 通常対局モードのはずなので現在の持ち時間設定を渡してやる。

            var limit = GameMode.IsWithEngine() ?
                UsiThinkLimit.TimeLimitLess : 
                UsiThinkLimit.FromTimeSetting(PlayTimers, stm);

            stmPlayer.Think(usiPosition , limit);

            // -- 検討ウィンドウに対して、ここをrootSfenとして設定
            if (ThinkReportEnable && isUsiEngine)
            {
                ThinkReport = new UsiThinkReportMessage()
                {
                    type = UsiEngineReportMessageType.SetRootSfen,
                    number = NumberOfEngine == 1  ? 0 : (int)stm, // CPU1つなら1番目の窓、CPU2つならColorに相当する窓に
                    data = Position.ToSfen(),
                };
            }

            // 手番側のプレイヤーの時間消費を開始
            if (GameMode == GameModeEnum.InTheGame)
            {
                // InTheGame == trueならば、PlayerTimeSettingは適切に設定されているはず。
                // (対局開始時に初期化するので)

                PlayTimer(stm).ChangeToOurTurn();
            }

            // 非手番側のCanMoveをfalseに

            var nextPlayer = Player(stm.Not());
            nextPlayer.CanMove = false;

            // -- 手番が変わった時の各種propertyの更新

            EngineTurn = stmPlayer.PlayerType == PlayerTypeEnum.UsiEngine;
            // 対局中でなければ自由に動かせる。対局中は人間のプレイヤーでなければ駒を動かせない。
            CanUserMove = stmPlayer.PlayerType == PlayerTypeEnum.Human || GameMode.CanUserMove();

            // 値が変わっていなくとも変更通知を送りたいので自力でハンドラを呼び出す。
            RaisePropertyChanged("TurnChanged", CanUserMove); // 仮想プロパティ"TurnChanged"
        }

        /// <summary>
        /// 時間チェック
        /// </summary>
        private void CheckTime()
        {
            // エンジンの初期化中であるか。この時は、時間消費は行わない。
            UpdateInitializing();

            // 双方の残り時間表示の更新
            UpdateTimeString();

            // 時間切れ判定(対局中かつ手番側のみ)
            var stm = Position.sideToMove;
            if (GameMode == GameModeEnum.InTheGame && PlayTimer(stm).IsTimeUp())
                Player(stm).SpecialMove = Move.TIME_UP;
        }

        /// <summary>
        /// 残り時間の更新
        /// </summary>
        /// <param name="c"></param>
        private void UpdateTimeString()
        {
            // 前回と同じ文字列であれば実際は描画ハンドラが呼び出されないので問題ない。
            foreach (var c in All.Colors())
            {
                var ct = PlayTimer(c);
                SetRestTimeString(c, ct.DisplayShortString());
            }
        }

        /// <summary>
        /// ゲームの終了処理
        /// </summary>
        private void GameEnd()
        {
            // 対局中だったものが終了したのか？
            if (GameMode == GameModeEnum.InTheGame)
            {
                // 音声:「ありがとうございました。またお願いします。」
                TheApp.app.soundManager.ReadOut(SoundEnum.End);
            }

            GameMode = GameModeEnum.ConsiderationWithoutEngine;

            // 時間消費、停止
            foreach (var c in All.Colors())
                PlayTimer(c).StopTimer();

            // 棋譜ウィンドウ、勝手に書き換えられると困るのでこれでfixさせておく。
            kifuManager.EnableKifuList = false;

            // 連続対局が設定されている時はDisconnect()はせずに、ここで次の対局のスタートを行う。
            // (エンジンを入れ替えたりしないといけない)

            // 連続対局でないなら..
            Disconnect();

            // 手番が変わったことを通知。
            NotifyTurnChanged();
        }

        /// <summary>
        /// エンジンなどの切断処理
        /// </summary>
        private void Disconnect()
        {
            // Playerの終了処理をしてNullPlayerを突っ込んでおく。

            foreach (var c in All.Colors())
            {
                Players[(int)c].Dispose();
                Players[(int)c] = new NullPlayer();
            }
        }

        /// <summary>
        /// [Worker Thread] : 検討モードに入る。
        /// GameModeのsetterから呼び出される。
        /// </summary>
        private void StartConsideration()
        {
            CanUserMove = true;

            // 検討モード用のプレイヤーセッティングを行う。
            {
                var setting = new GameSetting();

                switch (GameMode)
                {
                    // 検討用エンジン
                    case GameModeEnum.ConsiderationWithEngine:
                        setting.Player(Color.BLACK).PlayerName = "検討エンジン";
                        setting.Player(Color.BLACK).IsCpu = true;
                        Players[0 /*検討用のプレイヤー*/ ] = PlayerBuilder.Create(PlayerTypeEnum.UsiEngine);
                        break;

                    // 詰将棋エンジン
                    case GameModeEnum.ConsiderationWithMateEngine:
                        setting.Player(Color.BLACK).PlayerName = "詰将棋エンジン";
                        setting.Player(Color.BLACK).IsCpu = true;
                        Players[0 /* 詰将棋用のプレイヤー */] = PlayerBuilder.Create(PlayerTypeEnum.UsiEngine);
                        break;
                }
                GameSetting = setting;
            }

            // 局面の設定
            kifuManager.EnableKifuList = false;

            // 検討ウィンドウへの読み筋などのリダイレクトを設定
            InitEngineConsiderationInfo(GameMode);
        }

        /// <summary>
        /// [Worker Thread] : 検討モードを抜けるコマンド
        /// GameModeのsetterから呼び出される。
        /// </summary>
        private void EndConsideration()
        {
            // disconnect the consideration engine
            Disconnect();
        }



        #endregion
    }
}
