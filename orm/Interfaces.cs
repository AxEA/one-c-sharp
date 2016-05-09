﻿using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Data;

namespace Zhichkin.ORM
{
    public interface IPersistent
    {
        PersistentState State { get; }
        event StateChangingEventHandler StateChanging;
        event StateChangedEventHandler StateChanged;
        void Save();
        void Kill();
        void Load();
        event SavingEventHandler Saving;
        event SavedEventHandler Saved;
        event KillingEventHandler Killing;
        event KilledEventHandler Killed;
        event LoadedEventHandler Loaded;
    }

    public interface IValueObject : IPersistent { }

    public interface IReferenceObject : IPersistent
    {
        Guid Identity { get; }
    }

    public interface IDataMapper
    {
        void Insert(IPersistent entity);
        void Select(IPersistent entity);
        void Update(IPersistent entity);
        void Delete(IPersistent entity);
    }

    public interface IIdentityMap
    {
        void Add(IReferenceObject item);
        bool Get(Guid identity, ref IReferenceObject item);
    }

    public interface IReferenceObjectFactory
    {
        IReferenceObject New(Type type); // New
        IReferenceObject New(Type type, Guid identity); // New
        IReferenceObject New(int typeCode, Guid identity); // Virtual
    }
}
