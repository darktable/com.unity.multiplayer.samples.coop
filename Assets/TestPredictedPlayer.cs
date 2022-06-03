using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

public class TestPredictedPlayer : NetworkBehaviour
{
    [SerializeField]
    float m_Speed = 5;

    public struct PredictedTransform : INetworkSerializable
    {
        const double PredictedSigma = 0.0001f; // will vary according to your gameplay

        public Vector3 Position;
        public Tick TickSent;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Position);
            serializer.SerializeValue(ref TickSent);
        }

        public bool Equals(PredictedTransform other)
        {
            return PositionEquals(Position, other.Position);
        }



        public static bool PositionEquals(Vector3 lhs, Vector3 rhs)
        {
            float num1 = lhs.x - rhs.x;
            float num2 = lhs.y - rhs.y;
            float num3 = lhs.z - rhs.z;
            return (double)num1 * (double)num1 + (double)num2 * (double)num2 + (double)num3 * (double)num3 < PredictedSigma; // adapted from Vector3 == operator
        }
    }

    public struct PredictedInput : INetworkSerializable
    {
        // todo use bitfield to serialize this
        public bool Forward;
        public bool Backward;
        public bool StraffLeft;
        public bool StraffRight;
        public bool isSet; // was there any inputs for that tick

        public Tick TickSent;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Forward);
            serializer.SerializeValue(ref Backward);
            serializer.SerializeValue(ref StraffLeft);
            serializer.SerializeValue(ref StraffRight);
            serializer.SerializeValue(ref isSet);
            serializer.SerializeValue(ref TickSent);
        }
    }

    NetworkVariable<PredictedTransform> m_ServerTransform = new(); // always late by x ms. GameObject's transform.position contains predicted value

    public struct Tick : INetworkSerializable, IEquatable<Tick>
    {
        public int Value;

        public Tick(int value)
        {
            Value = value;
        }

        public static implicit operator int(Tick d) => d.Value;
        public static implicit operator Tick(int value) => new Tick() { Value = value };

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Value);
        }

        public bool Equals(Tick other)
        {
            return Value == other.Value;
        }

        public override bool Equals(object obj)
        {
            return obj is Tick other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Value;
        }

        public override string ToString()
        {
            return Value.ToString(); // for debug
        }
    }

    List<PredictedInput> m_PredictedInputs = new();
    Dictionary<Tick, PredictedTransform> m_PredictedTransforms = new();
    List<PredictedInput> m_ServerReceivedBufferedInput = new();

    private static Tick CurrentLocalTick => NetworkManager.Singleton.NetworkTickSystem.LocalTime.Tick;

    void Awake()
    {
        NetworkManager.Singleton.NetworkTickSystem.Tick += NetworkTick;
        m_ServerTransform.OnValueChanged += OnServerTransformValueChanged;
    }

    void NetworkTick()
    {
        DebugPrint("NetworkTick begin");

        if (IsClient)
        {
            // check for local inputs
            CheckForLocalInput(out var input);

            // save inputs in input history
            m_PredictedInputs.Add(input);

            // send inputs
            SendInputServerRpc(input);

            // predict input on local transform
            var changedTransform = PredictOneTick(input, CurrentLocalTick);

            // save transform in history
            m_PredictedTransforms.Add(changedTransform.TickSent, changedTransform);
        }

        if (IsServer)
        {
            // check for received buffered inputs and move according to that input
            foreach (var input in m_ServerReceivedBufferedInput) // TODO this is not secure and could allow cheating
            {
                var changedTransform = m_ServerTransform.Value;
                MoveTick(input, ref changedTransform, this, CurrentLocalTick);

                m_ServerTransform.Value = changedTransform; // TODO don't change tick value if rest is not changed?
            }

            m_ServerReceivedBufferedInput.Clear();
        }
        DebugPrint("NetworkTick end");
    }

    PredictedTransform PredictOneTick(PredictedInput input, Tick tick)
    {
        var changedTransform = new PredictedTransform() { Position = transform.position };
        MoveTick(input, ref changedTransform, this, tick);
        transform.position = changedTransform.Position;
        return changedTransform;
    }

    [ServerRpc]
    void SendInputServerRpc(PredictedInput input)
    {
        m_ServerReceivedBufferedInput.Add(input);
    }

    void CheckForLocalInput(out PredictedInput input)
    {
        input = new PredictedInput();
        input.TickSent = NetworkManager.Singleton.NetworkTickSystem.LocalTime.Tick;
        if (Input.GetKey(KeyCode.W))
        {
            input.isSet = true;
            input.Forward = true;
        }

        if (Input.GetKey(KeyCode.S))
        {
            input.isSet = true;
            input.Backward = true;
        }

        if (Input.GetKey(KeyCode.A))
        {
            input.isSet = true;
            input.StraffLeft = true;
        }

        if (Input.GetKey(KeyCode.D))
        {
            input.isSet = true;
            input.StraffRight = true;
        }
    }

    void DebugPrint(string context)
    {
        var toPrint = context + "\n PredictedInputs: ";
        foreach (var input in m_PredictedInputs)
        {
            toPrint += $"{input.TickSent} ";
        }

        toPrint += "\nPredictedTransforms: ";
        var sorted = m_PredictedTransforms.Values.ToArray().OrderBy(predictedTransform => predictedTransform.TickSent.Value);
        foreach (var predictedTransform in sorted)
        {
            toPrint += $"{predictedTransform.TickSent} ";
        }

        toPrint += $"\n current tick: {CurrentLocalTick}";
        toPrint += $"\n last tick received: {m_ServerTransform.Value.TickSent}";
        Debug.Log(toPrint);
    }

    void OnServerTransformValueChanged(PredictedTransform previousValue, PredictedTransform newValue)
    {
        var beforePosition = transform.position; // debug;
        // mispredicted?
        DebugPrint("OnServerTransformValueChanged begin");
        var foundInHistory = m_PredictedTransforms.TryGetValue(newValue.TickSent, out var historyTransform);
        if (!(foundInHistory && newValue.Equals(historyTransform)))
        {
            // if mispredicted, correct
            // replace transform with newValue // todo issue with physics? stop physics for that operation?
            transform.position = newValue.Position;

            // go through inputs and apply on transform
            foreach (var oneInputFromHistory in m_PredictedInputs)
            {
                var oneTick = oneInputFromHistory.TickSent;
                if (oneTick > newValue.TickSent) // TODO there has to be something more efficient than this
                {
                    var newTransform = PredictOneTick(oneInputFromHistory, oneTick);
                    m_PredictedTransforms[oneTick] = newTransform;
                }
            }
        }

        DebugPrint("OnServerTransformValueChanged before history clean");

        // clear history for acked transforms and inputs
        for (var i = m_PredictedInputs.Count - 1; i >= 0; i--)
        {
            if (m_PredictedInputs[i].TickSent <= newValue.TickSent)
            {
                var tickToRemove = m_PredictedInputs[i].TickSent;
                m_PredictedInputs.RemoveAt(i);
                m_PredictedTransforms.Remove(tickToRemove);
            }
        }
        DebugPrint("OnServerTransformValueChanged end");
        if (beforePosition != transform.position)
        {
            Debug.Log("moved");
        }
    }

    static void MoveTick(PredictedInput input, ref PredictedTransform transform, TestPredictedPlayer self, Tick tick)
    {
        // todo issues with sound? with physics?
        var newPos = transform.Position;
        var deltaTime = 1f / NetworkManager.Singleton.NetworkTickSystem.TickRate;

        if (input.Forward)
        {
            newPos.z = newPos.z + self.m_Speed * deltaTime;
        }

        if (input.Backward)
        {
            newPos.z = newPos.z - self.m_Speed * deltaTime;
        }

        if (input.StraffLeft)
        {
            newPos.x = newPos.x - self.m_Speed * deltaTime;
        }

        if (input.StraffRight)
        {
            newPos.x = newPos.x + self.m_Speed * deltaTime;
        }

        transform.Position = newPos;
        transform.TickSent = tick;
    }


}
