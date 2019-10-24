﻿/*
 * ExternalReceiver
 * https://sabowl.sakura.ne.jp/gpsnmeajp/
 *
 * MIT License
 * 
 * Copyright (c) 2019 gpsnmeajp
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Profiling;
using VRM;

namespace EVMC4U
{
    //デイジーチェーン受信の最低限のインターフェース
    public interface IExternalReceiver
    {
        void MessageDaisyChain(ref uOSC.Message message, int callCount);
    }

    //キーボード入力情報
    public struct KeyInput
    {
        public int active;
        public string name;
        public int keycode;
    }

    //コントローラ入力情報
    public struct ControllerInput
    {
        public int active;
        public string name;
        public int IsLeft;
        public int IsTouch;
        public int IsAxis;
        public Vector3 Axis;
    }

    //イベント定義
    [Serializable]
    public class KeyInputEvent : UnityEvent<KeyInput> { };
    [Serializable]
    public class ControllerInputEvent : UnityEvent<ControllerInput> { };


    //[RequireComponent(typeof(uOSC.uOscServer))]
    public class ExternalReceiver : MonoBehaviour, IExternalReceiver
    {
        [Header("ExternalReceiver v3.0")]
        public GameObject Model = null;

        [Header("Root Synchronize Option")]
        public Transform RootTransform = null;
        public bool RootPositionSynchronize = true; //ルート座標同期(ルームスケール移動)
        public bool RootRotationSynchronize = true; //ルート回転同期
        public bool RootScaleOffsetSynchronize = false; //MRスケール適用

        [Header("Other Synchronize Option")]
        public bool BlendShapeSynchronize = true; //表情等同期
        public bool BonePositionSynchronize = true; //ボーン位置適用(回転は強制)

        [Header("Synchronize Cutoff Option")]
        public bool HandPoseSynchronizeCutoff = false; //指状態反映オフ
        public bool EyeBoneSynchronizeCutoff = false; //目ボーン反映オフ

        [Header("UI Option")]
        public bool ShowInformation = false; //通信状態表示UI
        public bool StrictMode = false; //プロトコルチェックモード

        [Header("Lowpass Filter Option")]
        public bool BonePositionFilterEnable = false; //ボーン位置フィルタ
        public bool BoneRotationFilterEnable = false; //ボーン回転フィルタ
        public float BoneFilter = 0.7f; //ボーンフィルタ係数

        public bool CameraPositionFilterEnable = false; //カメラ位置フィルタ(手ブレ補正)
        public bool CameraRotationFilterEnable = false; //カメラ回転フィルタ(手ブレ補正)
        public float CameraFilter = 0.95f; //カメラフィルタ係数

        [Header("Status")]
        [SerializeField]
        private string StatusMessage = ""; //状態メッセージ(Inspector表示用)
        [Header("Camera Control")]
        public Camera VMCControlledCamera = null; //VMCカメラ制御同期

        [Header("Daisy Chain")]
        public GameObject NextReceiver = null; //デイジーチェーン

        [Header("Event Callback")]
        public KeyInputEvent KeyInputAction = new KeyInputEvent(); //キーボード入力イベント
        public ControllerInputEvent ControllerInputAction = new ControllerInputEvent(); //コントローラボタンイベント


        public Transform TestPos1;
        public Transform TestPos2;
        public Transform TestPos3;

        //---Const---

        //rootパケット長定数(拡張判別)
        const int RootPacketLengthOfScaleAndOffset = 8;

        //---Private---
        IExternalReceiver NextReceiverInterface = null; //デイジーチェーンのインターフェース保持用(Start時に取得)

        //フィルタ用データ保持変数
        private Vector3[] bonePosFilter = new Vector3[Enum.GetNames(typeof(HumanBodyBones)).Length];
        private Quaternion[] boneRotFilter = new Quaternion[Enum.GetNames(typeof(HumanBodyBones)).Length];
        private Vector3 cameraPosFilter = Vector3.zero;
        private Quaternion cameraRotFilter = Quaternion.identity;

        //通信状態保持変数
        private int Available = 0; //データ送信可能な状態か
        private float time = 0; //送信時の時刻

        //モデル切替検出用reference保持変数
        private GameObject OldModel = null;

        //ボーン情報取得
        Animator animator = null;
        //VRMのブレンドシェーププロキシ
        VRMBlendShapeProxy blendShapeProxy = null;

        //ボーンENUM情報テーブル
        Dictionary<string, HumanBodyBones> HumanBodyBonesTable = new Dictionary<string, HumanBodyBones>();

        //uOSCサーバー
        uOSC.uOscServer server = null;

        //エラー・無限ループ検出フラグ(trueで一切の受信を停止する)
        bool shutdown = false;

        //メッセージ処理一時変数struct(負荷対策)
        Vector3 pos;
        Quaternion rot;
        Vector3 scale;
        Vector3 offset;
        ControllerInput con;
        KeyInput key;

        //UI用位置Rect(負荷対策)
        readonly Rect rect1 = new Rect(0, 0, 120, 70);
        readonly Rect rect2 = new Rect(10, 20, 100, 30);
        readonly Rect rect3 = new Rect(10, 40, 100, 300);

        //負荷測定
        CustomSampler SampleOnDataReceived;
        CustomSampler SampleMessageDaisyChain;
        CustomSampler SampleMessageDaisyChain_Next;
        CustomSampler SampleProcessMessage;
        CustomSampler SampleProcessMessage_RootPos;
        CustomSampler SampleProcessMessage_BonePos;
        CustomSampler SampleProcessMessage_BlendShape;
        CustomSampler SampleProcessMessage_CameraPosFOV;
        CustomSampler SampleProcessMessage_KeyEvent;
        CustomSampler SampleProcessMessage_ControllerEvent;
        CustomSampler SampleBoneSynchronize;
        CustomSampler SampleBoneSynchronizeSingle;
        CustomSampler SampleHumanBodyBonesTryParse;

        void Start()
        {
            SampleOnDataReceived = CustomSampler.Create("OnDataReceived");
            SampleMessageDaisyChain = CustomSampler.Create("MessageDaisyChain");
            SampleMessageDaisyChain_Next = CustomSampler.Create("MessageDaisyChain");
            SampleProcessMessage = CustomSampler.Create("ProcessMessage");
            SampleProcessMessage_RootPos = CustomSampler.Create("RootPos");
            SampleProcessMessage_BonePos = CustomSampler.Create("BonePos");
            SampleProcessMessage_BlendShape = CustomSampler.Create("BlendShape");
            SampleProcessMessage_CameraPosFOV = CustomSampler.Create("CameraPosFOV");
            SampleProcessMessage_KeyEvent = CustomSampler.Create("KeyEvent");
            SampleProcessMessage_ControllerEvent = CustomSampler.Create("ControllerEvent");
            SampleBoneSynchronize = CustomSampler.Create("BoneSynchronize");
            SampleBoneSynchronizeSingle = CustomSampler.Create("BoneSynchronizeSingle");
            SampleHumanBodyBonesTryParse = CustomSampler.Create("HumanBodyBonesTryParse");

            //NextReciverのインターフェースを取得する
            //インターフェースではInspectorに登録できないためGameObjectにしているが、毎度GetComponentすると重いため
            if (NextReceiver != null) {
                NextReceiverInterface = NextReceiver.GetComponent(typeof(IExternalReceiver)) as IExternalReceiver;
            }

            //サーバーを取得
            server = GetComponent<uOSC.uOscServer>();
            if (server)
            {
                //サーバーを初期化
                StatusMessage = "Waiting for VMC...";
                server.onDataReceived.AddListener(OnDataReceived);
            }
            else
            {
                //デイジーチェーンスレーブモード
                StatusMessage = "Waiting for Master...";
            }
        }

        //外部から通信状態を取得するための公開関数
        int GetAvailable()
        {
            return Available;
        }

        //外部から通信時刻を取得するための公開関数
        float GetRemoteTime()
        {
            return time;
        }

        //通信状態表示用UI
        void OnGUI()
        {
            if (ShowInformation)
            {
                GUI.TextField(rect1, "ExternalReceiver");
                GUI.Label(rect2, "Available: " + GetAvailable());
                GUI.Label(rect3, "Time: " + GetRemoteTime());
            }
        }

        void Update()
        {
            //エラー・無限ループ時は処理をしない
            if (shutdown) { return; }

            //5.6.3p1などRunInBackgroundが既定で無効な場合Unityが極めて重くなるため対処
            Application.runInBackground = true;

            //VRMモデルからBlendShapeProxyを取得(タイミングの問題)
            if (blendShapeProxy == null && Model != null)
            {
                blendShapeProxy = Model.GetComponent<VRMBlendShapeProxy>();
            }

            //ルート姿勢がない場合
            if (RootTransform == null && Model != null)
            {
                //モデル姿勢をルート姿勢にする
                RootTransform = Model.transform;
            }

            //モデルがない場合はエラー表示をしておく(親切心)
            if (Model == null)
            {
                StatusMessage = "Model not found.";
                return;
            }
        }

        //データ受信イベント
        private void OnDataReceived(uOSC.Message message)
        {
            //チェーン数0としてデイジーチェーンを発生させる
            SampleOnDataReceived.Begin();
            MessageDaisyChain(ref message, 0);
            SampleOnDataReceived.End();
        }

        //デイジーチェーン処理
        public void MessageDaisyChain(ref uOSC.Message message, int callCount)
        {
            SampleMessageDaisyChain.Begin();
            //エラー・無限ループ時は処理をしない
            if (shutdown) {
                SampleMessageDaisyChain.End();
                return;
            }

            //メッセージを処理
            ProcessMessage(ref message);

            //次のデイジーチェーンへ伝える
            SampleMessageDaisyChain_Next.Begin();
            if (NextReceiver != null)
            {
                //100回以上もChainするとは考えづらい
                if (callCount > 100)
                {
                    //無限ループ対策
                    Debug.LogError("[ExternalReceiver] Too many call(maybe infinite loop).");
                    StatusMessage = "Infinite loop detected!";

                    //以降の処理を全部停止
                    shutdown = true;
                }
                else
                {
                    //インターフェースがあるか
                    if (NextReceiverInterface != null)
                    {
                        //Chain数を+1して次へ
                        NextReceiverInterface.MessageDaisyChain(ref message, callCount + 1);
                    }
                    else {
                        //GameObjectはあるがIExternalReceiverじゃないのでnullにする
                        NextReceiver = null;
                        Debug.LogError("[ExternalReceiver] NextReceiver not implemented IExternalReceiver. set null");
                    }
                }
            }
            SampleMessageDaisyChain_Next.End();
            SampleMessageDaisyChain.End();
        }

        //メッセージ処理本体
        private void ProcessMessage(ref uOSC.Message message)
        {
            SampleProcessMessage.Begin();
            //メッセージアドレスがない、あるいはメッセージがない不正な形式の場合は処理しない
            if (message.address == null || message.values == null)
            {
                StatusMessage = "Bad message.";

                //厳格モード
                if (StrictMode)
                {
                    //プロトコルにないアドレスを検出したら以後の処理を一切しない
                    //ほぼデバッグ用
                    Debug.LogError("[ExternalReceiver] null message received");
                    shutdown = true;
                }

                SampleProcessMessage.End();
                return;
            }

            //ルート姿勢がない場合
            if (RootTransform == null && Model != null)
            {
                //モデル姿勢をルート姿勢にする
                RootTransform = Model.transform;
            }

            //モデルがないか、モデル姿勢、ルート姿勢が取得できないなら何もしない
            if (Model == null || Model.transform == null || RootTransform == null)
            {
                SampleProcessMessage.End();
                return;
            }

            //モーションデータ送信可否
            if (message.address == "/VMC/Ext/OK"
                && (message.values[0] is int))
            {
                Available = (int)message.values[0];
                if (Available == 0)
                {
                    StatusMessage = "Waiting for [Load VRM]";
                }
            }
            //データ送信時刻
            else if (message.address == "/VMC/Ext/T"
                && (message.values[0] is float))
            {
                time = (float)message.values[0];
            }

            //Root姿勢
            else if (message.address == "/VMC/Ext/Root/Pos"
                && (message.values[0] is string)
                && (message.values[1] is float)
                && (message.values[2] is float)
                && (message.values[3] is float)
                && (message.values[4] is float)
                && (message.values[5] is float)
                && (message.values[6] is float)
                && (message.values[7] is float)
                )
            {
                SampleProcessMessage_RootPos.Begin();

                StatusMessage = "OK";

                pos.x = (float)message.values[1];
                pos.y = (float)message.values[2];
                pos.z = (float)message.values[3];
                rot.x = (float)message.values[4];
                rot.y = (float)message.values[5];
                rot.z = (float)message.values[6];
                rot.w = (float)message.values[7];

                //位置同期
                if (RootPositionSynchronize)
                {
                    RootTransform.localPosition = pos;
                }
                //回転同期
                if (RootRotationSynchronize)
                {
                    RootTransform.localRotation = rot;
                }
                //スケール同期とオフセット補正(v2.1拡張プロトコルの場合のみ)
                if (RootScaleOffsetSynchronize && message.values.Length > RootPacketLengthOfScaleAndOffset
                    && (message.values[8] is float)
                    && (message.values[9] is float)
                    && (message.values[10] is float)
                    && (message.values[11] is float)
                    && (message.values[12] is float)
                    && (message.values[13] is float)
                    )
                {
                    scale.x = 1.0f / (float)message.values[8];
                    scale.y = 1.0f / (float)message.values[9];
                    scale.z = 1.0f / (float)message.values[10];
                    offset.x = (float)message.values[11];
                    offset.y = (float)message.values[12];
                    offset.z = (float)message.values[13];

                    RootTransform.localScale = scale;
                    RootTransform.position -= offset;
                }

                SampleProcessMessage_RootPos.End();
            }
            //ボーン姿勢
            else if (message.address == "/VMC/Ext/Bone/Pos"
                && (message.values[0] is string)
                && (message.values[1] is float)
                && (message.values[2] is float)
                && (message.values[3] is float)
                && (message.values[4] is float)
                && (message.values[5] is float)
                && (message.values[6] is float)
                && (message.values[7] is float)
                )
            {
                SampleProcessMessage_BonePos.Begin();

                pos.x = (float)message.values[1];
                pos.y = (float)message.values[2];
                pos.z = (float)message.values[3];
                rot.x = (float)message.values[4];
                rot.y = (float)message.values[5];
                rot.z = (float)message.values[6];
                rot.w = (float)message.values[7];

                BoneSynchronize((string)message.values[0], ref pos, ref rot);
                SampleProcessMessage_BonePos.End();
            }

            //ブレンドシェープ同期
            else if (message.address == "/VMC/Ext/Blend/Val"
                && (message.values[0] is string)
                && (message.values[1] is float)
                )
            {
                SampleProcessMessage_BlendShape.Begin();

                if (BlendShapeSynchronize && blendShapeProxy != null)
                {
                    blendShapeProxy.AccumulateValue((string)message.values[0], (float)message.values[1]);
                }

                SampleProcessMessage_BlendShape.End();
            }
            //ブレンドシェープ適用
            else if (message.address == "/VMC/Ext/Blend/Apply")
            {
                SampleProcessMessage_BlendShape.Begin();

                if (BlendShapeSynchronize && blendShapeProxy != null)
                {
                    blendShapeProxy.Apply();
                }

                SampleProcessMessage_BlendShape.End();
            }
            //カメラ姿勢FOV同期 v2.1
            else if (message.address == "/VMC/Ext/Cam"
                && (message.values[0] is string)
                && (message.values[1] is float)
                && (message.values[2] is float)
                && (message.values[3] is float)
                && (message.values[4] is float)
                && (message.values[5] is float)
                && (message.values[6] is float)
                && (message.values[7] is float)
                && (message.values[8] is float)
                )
            {
                SampleProcessMessage_CameraPosFOV.Begin();

                //カメラがセットされているならば
                if (VMCControlledCamera != null && VMCControlledCamera.transform != null)
                {
                    pos.x = (float)message.values[1];
                    pos.y = (float)message.values[2];
                    pos.z = (float)message.values[3];
                    rot.x = (float)message.values[4];
                    rot.y = (float)message.values[5];
                    rot.z = (float)message.values[6];
                    rot.w = (float)message.values[7];
                    float fov = (float)message.values[8];

                    //カメラ移動フィルタ
                    if (CameraPositionFilterEnable)
                    {
                        cameraPosFilter = (cameraPosFilter * CameraFilter) + pos * (1.0f - CameraFilter);
                        VMCControlledCamera.transform.localPosition = cameraPosFilter;
                    }
                    else {
                        VMCControlledCamera.transform.localPosition = pos;
                    }
                    //カメラ回転フィルタ
                    if (CameraRotationFilterEnable)
                    {
                        cameraRotFilter = Quaternion.Slerp(cameraRotFilter, rot, 1.0f - CameraFilter);
                        VMCControlledCamera.transform.localRotation = cameraRotFilter;
                    }
                    else {
                        VMCControlledCamera.transform.localRotation = rot;
                    }
                    //FOV同期
                    VMCControlledCamera.fieldOfView = fov;
                }

                SampleProcessMessage_CameraPosFOV.End();
            }
            //コントローラ操作情報 v2.1
            else if (message.address == "/VMC/Ext/Con"
                && (message.values[0] is int)
                && (message.values[1] is string)
                && (message.values[2] is int)
                && (message.values[3] is int)
                && (message.values[4] is int)
                && (message.values[5] is float)
                && (message.values[6] is float)
                && (message.values[7] is float)
                )
            {
                SampleProcessMessage_ControllerEvent.Begin();

                con.active = (int)message.values[0];
                con.name = (string)message.values[1];
                con.IsLeft = (int)message.values[2];
                con.IsTouch = (int)message.values[3];
                con.IsAxis = (int)message.values[4];
                con.Axis.x = (float)message.values[5];
                con.Axis.y = (float)message.values[6];
                con.Axis.z = (float)message.values[7];

                //イベントを呼び出す
                if (ControllerInputAction != null) {
                    ControllerInputAction.Invoke(con);
                }

                SampleProcessMessage_ControllerEvent.End();
            }
            //キーボード操作情報 v2.1
            else if (message.address == "/VMC/Ext/Key"
                && (message.values[0] is int)
                && (message.values[1] is string)
                && (message.values[2] is int)
                )
            {
                SampleProcessMessage_KeyEvent.Begin();

                key.active = (int)message.values[0];
                key.name = (string)message.values[1];
                key.keycode = (int)message.values[2];

                //イベントを呼び出す
                if (KeyInputAction != null)
                {
                    KeyInputAction.Invoke(key);
                }

                SampleProcessMessage_KeyEvent.End();
            }
            // v2.2
            else if (message.address == "/VMC/Ext/Midi/Note"
                && (message.values[0] is int)
                && (message.values[1] is int)
                && (message.values[2] is int)
                && (message.values[3] is float)
                )
            {
                Debug.Log("Note " + (int)message.values[0] + "/" + (int)message.values[1] + "/" + (int)message.values[2] + "/" + (float)message.values[3]);
            }
            // v2.2
            else if (message.address == "/VMC/Ext/Midi/CC/Val"
                && (message.values[0] is int)
                && (message.values[1] is float)
                )
            {
                Debug.Log("CC Val " + (int)message.values[0] + "/" + (float)message.values[1]);
            }
            // v2.2
            else if (message.address == "/VMC/Ext/Midi/CC/Bit"
                && (message.values[0] is int)
                && (message.values[1] is int)
                )
            {
                Debug.Log("CC Bit " + (int)message.values[0] + "/" + (int)message.values[1]);
            }
            // v2.2
            else if (message.address == "/VMC/Ext/Hmd/Pos"
                && (message.values[0] is string)
                && (message.values[1] is float)
                && (message.values[2] is float)
                && (message.values[3] is float)
                && (message.values[4] is float)
                && (message.values[5] is float)
                && (message.values[6] is float)
                && (message.values[7] is float)
                )
            {
                pos.x = (float)message.values[1];
                pos.y = (float)message.values[2];
                pos.z = (float)message.values[3];
                rot.x = (float)message.values[4];
                rot.y = (float)message.values[5];
                rot.z = (float)message.values[6];
                rot.w = (float)message.values[7];

                TestPos1.position = pos;
                TestPos1.rotation = rot;

                Debug.Log("HMD pos "+ (string)message.values[0] +" : "+ pos+"/"+rot);
            }
            // v2.2
            else if (message.address == "/VMC/Ext/Con/Pos"
                && (message.values[0] is string)
                && (message.values[1] is float)
                && (message.values[2] is float)
                && (message.values[3] is float)
                && (message.values[4] is float)
                && (message.values[5] is float)
                && (message.values[6] is float)
                && (message.values[7] is float)
                )
            {
                pos.x = (float)message.values[1];
                pos.y = (float)message.values[2];
                pos.z = (float)message.values[3];
                rot.x = (float)message.values[4];
                rot.y = (float)message.values[5];
                rot.z = (float)message.values[6];
                rot.w = (float)message.values[7];

                TestPos2.position = pos;
                TestPos2.rotation = rot;

                Debug.Log("Con pos " + (string)message.values[0] + " : " + pos + "/" + rot);
            }
            // v2.2
            else if (message.address == "/VMC/Ext/Tra/Pos"
                && (message.values[0] is string)
                && (message.values[1] is float)
                && (message.values[2] is float)
                && (message.values[3] is float)
                && (message.values[4] is float)
                && (message.values[5] is float)
                && (message.values[6] is float)
                && (message.values[7] is float)
                )
            {
                pos.x = (float)message.values[1];
                pos.y = (float)message.values[2];
                pos.z = (float)message.values[3];
                rot.x = (float)message.values[4];
                rot.y = (float)message.values[5];
                rot.z = (float)message.values[6];
                rot.w = (float)message.values[7];

                TestPos3.position = pos;
                TestPos3.rotation = rot;

                Debug.Log("Tra pos " + (string)message.values[0] + " : " + pos + "/" + rot);
            }
            else
            {
                //厳格モード
                if (StrictMode) {
                    //プロトコルにないアドレスを検出したら以後の処理を一切しない
                    //ほぼデバッグ用
                    Debug.LogError("[ExternalReceiver] " + message.address + " is not valid");
                    StatusMessage = "Communication error.";
                    shutdown = true;
                }
            }
            SampleProcessMessage.End();
        }

        //ボーン位置同期
        private void BoneSynchronize(string boneName, ref Vector3 pos, ref Quaternion rot)
        {
            SampleBoneSynchronize.Begin();

            //モデルが更新されたときに関連情報を更新する
            if (OldModel != Model && Model != null)
            {
                animator = Model.GetComponent<Animator>();
                blendShapeProxy = Model.GetComponent<VRMBlendShapeProxy>();
                OldModel = Model;
                Debug.Log("[ExternalReceiver] New model detected");
            }

            //Humanoidボーンに該当するボーンがあるか調べる
            HumanBodyBones bone;
            if (HumanBodyBonesTryParse(ref boneName, out bone))
            {
                //操作可能な状態かチェック
                if (animator != null && bone != HumanBodyBones.LastBone)
                {
                    //ボーンによって操作を分ける
                    var t = animator.GetBoneTransform(bone);
                    if (t != null)
                    {
                        //指ボーン
                        if (bone == HumanBodyBones.LeftIndexDistal ||
                            bone == HumanBodyBones.LeftIndexIntermediate ||
                            bone == HumanBodyBones.LeftIndexProximal ||
                            bone == HumanBodyBones.LeftLittleDistal ||
                            bone == HumanBodyBones.LeftLittleIntermediate ||
                            bone == HumanBodyBones.LeftLittleProximal ||
                            bone == HumanBodyBones.LeftMiddleDistal ||
                            bone == HumanBodyBones.LeftMiddleIntermediate ||
                            bone == HumanBodyBones.LeftMiddleProximal ||
                            bone == HumanBodyBones.LeftRingDistal ||
                            bone == HumanBodyBones.LeftRingIntermediate ||
                            bone == HumanBodyBones.LeftRingProximal ||
                            bone == HumanBodyBones.LeftThumbDistal ||
                            bone == HumanBodyBones.LeftThumbIntermediate ||
                            bone == HumanBodyBones.LeftThumbProximal ||

                            bone == HumanBodyBones.RightIndexDistal ||
                            bone == HumanBodyBones.RightIndexIntermediate ||
                            bone == HumanBodyBones.RightIndexProximal ||
                            bone == HumanBodyBones.RightLittleDistal ||
                            bone == HumanBodyBones.RightLittleIntermediate ||
                            bone == HumanBodyBones.RightLittleProximal ||
                            bone == HumanBodyBones.RightMiddleDistal ||
                            bone == HumanBodyBones.RightMiddleIntermediate ||
                            bone == HumanBodyBones.RightMiddleProximal ||
                            bone == HumanBodyBones.RightRingDistal ||
                            bone == HumanBodyBones.RightRingIntermediate ||
                            bone == HumanBodyBones.RightRingProximal ||
                            bone == HumanBodyBones.RightThumbDistal ||
                            bone == HumanBodyBones.RightThumbIntermediate ||
                            bone == HumanBodyBones.RightThumbProximal)
                        {
                            //指ボーンカットオフが有効でなければ
                            if (!HandPoseSynchronizeCutoff)
                            {
                                //ボーン同期する。ただしフィルタはかけない
                                BoneSynchronizeSingle(t, ref bone, ref pos, ref rot, false, false);
                            }
                        }
                        //目ボーン
                        else if (bone == HumanBodyBones.LeftEye ||
                            bone == HumanBodyBones.RightEye)
                        {
                            //目ボーンカットオフが有効でなければ
                            if (!EyeBoneSynchronizeCutoff)
                            {
                                //ボーン同期する。ただしフィルタはかけない
                                BoneSynchronizeSingle(t, ref bone, ref pos, ref rot, false, false);
                            }
                        }
                        else
                        {
                            //ボーン同期する。フィルタは設定依存
                            BoneSynchronizeSingle(t, ref bone, ref pos, ref rot, BonePositionFilterEnable, BoneRotationFilterEnable);
                        }
                    }
                }
            }
            SampleBoneSynchronize.End();
        }

        //1本のボーンの同期
        private void BoneSynchronizeSingle(Transform t, ref HumanBodyBones bone, ref Vector3 pos, ref Quaternion rot, bool posFilter, bool rotFilter)
        {
            SampleBoneSynchronizeSingle.Begin();

            //ボーン位置同期が有効か
            if (BonePositionSynchronize)
            {
                //ボーン位置フィルタが有効か
                if (posFilter)
                {
                    bonePosFilter[(int)bone] = (bonePosFilter[(int)bone] * BoneFilter) + pos * (1.0f - BoneFilter);
                    t.localPosition = bonePosFilter[(int)bone];
                }
                else
                {
                    t.localPosition = pos;
                }
            }

            //ボーン回転フィルタが有効か
            if (rotFilter)
            {
                boneRotFilter[(int)bone] = Quaternion.Slerp(boneRotFilter[(int)bone], rot, 1.0f - BoneFilter);
                t.localRotation = boneRotFilter[(int)bone];
            }
            else
            {
                t.localRotation = rot;
            }

            SampleBoneSynchronizeSingle.End();
        }

        //ボーンENUM情報をキャッシュして高速化
        private bool HumanBodyBonesTryParse(ref string boneName, out HumanBodyBones bone)
        {
            SampleHumanBodyBonesTryParse.Begin();

            //ボーンキャッシュテーブルに存在するなら
            if (HumanBodyBonesTable.ContainsKey(boneName))
            {
                //キャッシュテーブルから返す
                bone = HumanBodyBonesTable[boneName];
                //ただしLastBoneは発見しなかったことにする(無効値として扱う)
                if (bone == HumanBodyBones.LastBone) {

                    SampleHumanBodyBonesTryParse.End();
                    return false;
                }

                SampleHumanBodyBonesTryParse.End();
                return true;
            }
            else {
                //キャッシュテーブルにない場合、検索する
                var res = EnumTryParse<HumanBodyBones>(boneName, out bone);
                if (!res)
                {
                    //見つからなかった場合はLastBoneとして登録する(無効値として扱う)ことにより次回から検索しない
                    bone = HumanBodyBones.LastBone;
                }
                //キャシュテーブルに登録する
                HumanBodyBonesTable.Add(boneName, bone);

                SampleHumanBodyBonesTryParse.End();
                return res;
            }
        }

        //互換性を持ったTryParse
        private static bool EnumTryParse<T>(string value, out T result) where T : struct
        {
#if NET_4_6
            return Enum.TryParse(value, out result);
#else
            try
            {
                result = (T)Enum.Parse(typeof(T), value, true);
                return true;
            }
            catch
            {
                result = default(T);
                return false;
            }
#endif
        }

        //アプリケーションを終了させる
        public void ApplicationQuit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
