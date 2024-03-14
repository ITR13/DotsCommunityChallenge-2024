using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Assertions;

public partial struct RunCgl2 : ISystem
{
    private const byte PrePaddingBytes = 2;
    private const byte PostPaddingBytes = 2;


    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<CalcMode>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var calcMode = SystemAPI.GetSingleton<CalcMode>();
        if (calcMode.Algorithm != Algorithm.HashMap) return;

        var groupQuery = SystemAPI.QueryBuilder().WithAll<GroupPosition, CurrentCglGroup>().WithAllRW<NextCglGroup>().Build();

        var groups = new NativeHashMap<int2, Entity>(groupQuery.CalculateEntityCount(), state.WorldUpdateAllocator);

        state.Dependency = new PopulateGroupHashmapJob
        {
            Groups = groups,
        }.Schedule(state.Dependency);

        state.Dependency = new UpdateGroup
        {
            PositionTypeHandle = SystemAPI.GetComponentTypeHandle<GroupPosition>(true),
            CurrentTypeHandle = SystemAPI.GetComponentTypeHandle<CurrentCglGroup>(true),
            NextTypeHandle = SystemAPI.GetComponentTypeHandle<NextCglGroup>(),
            Groups = groups,
            CurrentLookup = SystemAPI.GetComponentLookup<CurrentCglGroup>(true),

            TopLeftOffset = new int2(-Constants.GroupTotalEdgeLength, -Constants.GroupTotalEdgeLength),
            TopCentOffset = new int2(0, -Constants.GroupTotalEdgeLength),
            TopRighOffset = new int2(+Constants.GroupTotalEdgeLength, -Constants.GroupTotalEdgeLength),
            MidLeftOffset = new int2(-Constants.GroupTotalEdgeLength, 0),
            // MidCentOffset = new int2(0, 0),
            MidRighOffset = new int2(+Constants.GroupTotalEdgeLength, 0),
            BotLeftOffset = new int2(-Constants.GroupTotalEdgeLength, +Constants.GroupTotalEdgeLength),
            BotCentOffset = new int2(0, +Constants.GroupTotalEdgeLength),
            BotRighOffset = new int2(+Constants.GroupTotalEdgeLength, +Constants.GroupTotalEdgeLength),
        }.ScheduleParallel(groupQuery, state.Dependency);

        if (!calcMode.SimulateStill)
        {
            state.Dependency = new RunCgl.SwapBuffersJob().ScheduleParallel(state.Dependency);
        }
    }

    [BurstCompile]
    public partial struct PopulateGroupHashmapJob : IJobEntity
    {
        public NativeHashMap<int2, Entity> Groups;

        private void Execute(in Entity entity, in GroupPosition groupPosition)
        {
            Groups[groupPosition.Position] = entity;
        }
    }

    [BurstCompile]
    private struct UpdateGroup : IJobChunk
    {
        public int2 TopLeftOffset;
        public int2 TopCentOffset;
        public int2 TopRighOffset;

        public int2 MidLeftOffset;

        // public int2 MidCentOffset;
        public int2 MidRighOffset;
        public int2 BotLeftOffset;
        public int2 BotCentOffset;
        public int2 BotRighOffset;

        [ReadOnly] public ComponentTypeHandle<CurrentCglGroup> CurrentTypeHandle;
        [ReadOnly] public ComponentTypeHandle<GroupPosition> PositionTypeHandle;
        public ComponentTypeHandle<NextCglGroup> NextTypeHandle;

        [ReadOnly] public NativeHashMap<int2, Entity> Groups;
        [ReadOnly] public ComponentLookup<CurrentCglGroup> CurrentLookup;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            Assert.IsFalse(useEnabledMask);

            var currentGroups = chunk.GetNativeArray(ref CurrentTypeHandle);
            var nextGroups = chunk.GetNativeArray(ref NextTypeHandle);
            var positions = chunk.GetNativeArray(ref PositionTypeHandle);

            // NB! One row uses 16 bits from 4 sequential SubGroups
            var neighbors = new NativeArray<byte>(Constants.GroupTotalArea + PrePaddingBytes + PostPaddingBytes, Allocator.Temp);

            // bit pattern
            var precalculated = new NativeArray<ulong>(16, Allocator.Temp);
            for (var i = 0; i < 16; i++)
            {
                precalculated[i] = (i is 0b1011 or 0b1100 or 0b0011) ? 1 : (ulong)0;
            }

            for (var i = 0; i < chunk.Count; i++)
            {
                var position = positions[i].Position;

                #region Courners

                {
                    if (Groups.TryGetValue(position + TopLeftOffset, out var currentNeighborEntity))
                    {
                        var currentNeighbor = CurrentLookup[currentNeighborEntity];
                        neighbors[PosToBitIndex(0, 0)] = (byte)((currentNeighbor.Data.Alive15 >> 0x3F) & 1);
                    }
                }

                {
                    if (Groups.TryGetValue(position + TopRighOffset, out var currentNeighborEntity))
                    {
                        var currentNeighbor = CurrentLookup[currentNeighborEntity];
                        neighbors[PosToBitIndex(Constants.GroupTotalEdgeLength - 1, 0)] = (byte)((currentNeighbor.Data.Alive12 >> 0x38) & 1);
                    }
                }

                {
                    if (Groups.TryGetValue(position + BotLeftOffset, out var currentNeighborEntity))
                    {
                        var currentNeighbor = CurrentLookup[currentNeighborEntity];
                        neighbors[PosToBitIndex(0, Constants.GroupTotalEdgeLength - 1)] = (byte)((currentNeighbor.Data.Alive3 >> 0x07) & 1);
                    }
                }

                {
                    if (Groups.TryGetValue(position + BotRighOffset, out var currentNeighborEntity))
                    {
                        var currentNeighbor = CurrentLookup[currentNeighborEntity];
                        neighbors[PosToBitIndex(Constants.GroupTotalEdgeLength - 1, Constants.GroupTotalEdgeLength - 1)] = (byte)(currentNeighbor.Data.Alive0 & 1);
                    }
                }

                #endregion

                #region UpperLowerEdges

                {
                    if (Groups.TryGetValue(position + TopCentOffset, out var currentNeighborEntity))
                    {
                        var currentNeighbor = CurrentLookup[currentNeighborEntity];
                        var topEdge = new BitField32(
                            (uint)(
                                (currentNeighbor.Data.Alive12 >> 0x38) & 0b00000000000000000000000011111111 |
                                (currentNeighbor.Data.Alive13 >> 0x30) & 0b00000000000000001111111100000000 |
                                (currentNeighbor.Data.Alive14 >> 0x28) & 0b00000000111111110000000000000000 |
                                (currentNeighbor.Data.Alive15 >> 0x20) & 0b11111111000000000000000000000000
                            )
                        );

                        unsafe
                        {
                            // We start this two before 0,0 for ease of use
                            var ptr = (byte*)neighbors.GetUnsafePtr(); /* + PosToBitIndex(0, 0) - 2 */ // +2-2 = 0;
                            PopulateCenterEdgeNeigbors(topEdge, ptr);
                        }
                    }
                }

                {
                    if (Groups.TryGetValue(position + BotCentOffset, out var currentNeighborEntity))
                    {
                        var currentNeighbor = CurrentLookup[currentNeighborEntity];
                        var bottomEdge = new BitField32(
                            (uint)(
                                (currentNeighbor.Data.Alive0 << 0x00) & 0b00000000000000000000000011111111 |
                                (currentNeighbor.Data.Alive1 << 0x08) & 0b00000000000000001111111100000000 |
                                (currentNeighbor.Data.Alive2 << 0x10) & 0b00000000111111110000000000000000 |
                                (currentNeighbor.Data.Alive3 << 0x18) & 0b11111111000000000000000000000000
                            )
                        );

                        unsafe
                        {
                            // We start this two before [max-1],0 for ease of use
                            var ptr = ((byte*)neighbors.GetUnsafePtr()) + PosToBitIndex(0, Constants.GroupTotalEdgeLength - 1) - 2;
                            PopulateCenterEdgeNeigbors(bottomEdge, ptr);
                        }
                    }
                }

                #endregion

                #region LeftRightEdges

                {
                    if (Groups.TryGetValue(position + MidLeftOffset, out var currentNeighborEntity))
                    {
                        var currentNeighbor = CurrentLookup[currentNeighborEntity];

                        // Oof, slow?
                        for (byte subgroup = 0; subgroup < Constants.GroupSize; subgroup++)
                        {
                            ulong value;
                            unsafe
                            {
                                value = ((ulong*)&currentNeighbor)[(subgroup + 1) * Constants.GroupSize - 1];
                            }

                            for (byte y = 0; y < 8; y++)
                            {
                                neighbors[PosToBitIndex(0, (subgroup + 1) * Constants.BitFieldSize - y - 1)] += (byte)((value >> (y * 8)) & 1);
                            }
                        }
                    }
                }

                {
                    if (Groups.TryGetValue(position + MidRighOffset, out var currentNeighborEntity))
                    {
                        var currentNeighbor = CurrentLookup[currentNeighborEntity];

                        for (byte subgroup = 0; subgroup < Constants.GroupSize; subgroup++)
                        {
                            ulong value;
                            unsafe
                            {
                                value = ((ulong*)&currentNeighbor)[subgroup * Constants.GroupSize];
                            }

                            for (byte y = 0; y < 8; y++)
                            {
                                neighbors[PosToBitIndex(Constants.BitFieldSize - 1, (subgroup + 1) * Constants.BitFieldSize - y - 1)] += (byte)((value >> (y * 8 + 7)) & 1);
                            }
                        }
                    }
                }

                #endregion

                {
                    var current = currentGroups[i];
                    for (var y = 0; y < Constants.GroupSize; y++)
                    {
                        for (var x = 0; x < Constants.GroupSize; x++)
                        {
                            ulong currentBitmask;
                            unsafe
                            {
                                currentBitmask = ((ulong*)&current)[y * Constants.GroupSize + x];
                            }

                            for (var by = 0; by < Constants.BitFieldSize && currentBitmask > 0; by++)
                            {
                                for (var bx = 0; bx < Constants.BitFieldSize; bx++)
                                {
                                    var value = currentBitmask & 1;
                                    var aliveFlag = (byte)(currentBitmask << 3);
                                    var addVal = (byte)value;

                                    var index = PosToBitIndex(x * Constants.BitFieldSize + bx, y * Constants.BitFieldSize + by);
                                    neighbors[index] += aliveFlag;

                                    neighbors[index - 1] += addVal;
                                    neighbors[index + 1] += addVal;
                                    if (y + by > 0)
                                    {
                                        neighbors[index - Constants.GroupTotalEdgeLength - 1] += addVal;
                                        neighbors[index - Constants.GroupTotalEdgeLength - 0] += addVal;
                                        neighbors[index - Constants.GroupTotalEdgeLength + 1] += addVal;
                                    }

                                    if (y != Constants.GroupSize - 1 && bx != Constants.BitFieldSize - 1)
                                    {
                                        neighbors[index + Constants.GroupTotalEdgeLength - 1] += addVal;
                                        neighbors[index + Constants.GroupTotalEdgeLength - 0] += addVal;
                                        neighbors[index + Constants.GroupTotalEdgeLength + 1] += addVal;
                                    }

                                    currentBitmask >>= 1;
                                }
                            }
                        }
                    }
                }

                {
                    var next = new NextCglGroup();
                    for (var y = 0; y < Constants.GroupSize; y++)
                    {
                        for (var x = 0; x < Constants.GroupSize; x++)
                        {
                            var nextBitmask = (ulong)0;
                            for (var by = 0; by < Constants.BitFieldSize; by++)
                            {
                                for (var bx = 0; bx < Constants.BitFieldSize; bx++)
                                {
                                    nextBitmask <<= 1;
                                    var index = PosToBitIndex(x * Constants.BitFieldSize + bx, y * Constants.BitFieldSize + by);
                                    nextBitmask |= precalculated[neighbors[index] & 0b1111];
                                    neighbors[index] = 0;
                                }
                            }

                            unsafe
                            {
                                ((ulong*)&next)[y * Constants.GroupSize + x] = nextBitmask;
                            }
                        }
                    }

                    nextGroups[i] = next;
                }
            }
        }

        private unsafe void PopulateCenterEdgeNeigbors(BitField32 line, byte* neighbors)
        {
            // The row consists of 32 bytes padded with 2 bytes on each side we shouldn't touch:
            // [x][x][0][1][2][4][5][6][7]..[^3][^2][^1][x][x]
            // and neighbor points to the x two to the right of 0

            // First we want to add to the bytes [x][x][0][1], so if line.GetBits(0) == 1 then we want the bytes 0,0,1,1
            // The inner ones are easy since it's always [x][n][n+1][n+2], so we need the bytes 0,1,1,1
            // Final one is tricky again and becomes [^2][^1][x][x] 

            // NB: Assumes small endian!
            //                  33333333222222221111111100000000
            const uint addR = 0b00000001000000010000000100000000;
            const uint addF = 0b00000001000000010000000000000000;
            const uint addL = 0b00000000000000010000000100000000;

            if (line.Value == 0) return;

            var firstAdds = line.GetBits(0) * addF;
            var lastAdds = line.GetBits(31) * addL;
            (*(ulong*)neighbors) += firstAdds;
            (*(ulong*)(neighbors + 31)) += lastAdds;

            for (var bit = 1; bit < 31; bit++)
            {
                (*(ulong*)(neighbors + bit)) += line.GetBits(bit) * addR;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int PosToBitIndex(int x, int y)
        {
            ThrowIfOutOfBounds(x, y);
            return y * Constants.GroupTotalEdgeLength + x + PrePaddingBytes;
        }

        [BurstDiscard]
        private void ThrowIfOutOfBounds(int x, int y)
        {
            if (x is < 0 or >= Constants.GroupTotalEdgeLength)
            {
                throw new ArgumentException($"x in {x},{y} is out of range 0->{Constants.GroupTotalEdgeLength}", nameof(x));
            }

            if (y is < 0 or >= Constants.GroupTotalEdgeLength)
            {
                throw new ArgumentException($"y in {x},{y} is out of range 0->{Constants.GroupTotalEdgeLength}", nameof(y));
            }
        }
    }
}