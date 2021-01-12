using System;

namespace LighthouseManager.Models.Characteristics
{
    public class Powerstate : ICharacteristic
    {
        public readonly PowerstateReadValues PowerstateReadValues = new();
        public readonly PowerstateWriteValues PowerstateWriteValues = new();

        public Guid GetGuid()
        {
            return Guid.Parse("00001525-1212-EFDE-1523-785FEABCD124");
        }
    }

    public class PowerstateReadValues
    {
        public byte AwakeLastSleeping = 9;
        public byte AwakeLastStandby = 11;
        public byte Sleeping = 0;
        public byte Standby = 2;
    }

    public class PowerstateWriteValues
    {
        public byte Sleep = 0;
        public byte Standby = 2;
        public byte Wake = 1;
    }
}