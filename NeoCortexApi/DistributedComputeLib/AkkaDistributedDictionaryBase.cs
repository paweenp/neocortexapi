﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using Akka.Actor;
using Akka.Configuration;
using System.Threading.Tasks;

namespace NeoCortexApi.DistributedComputeLib
{
    public abstract class AkkaDistributedDictionaryBase<TKey, TValue> : IDictionary<TKey, TValue>, IEnumerator<KeyValuePair<TKey, TValue>>
    {      

        protected AkkaDistributedDictConfig Config { get; }

        private Dictionary<TKey, TValue>[] dictList;

        private IActorRef[] dictActors;

        private int numElements = 0;

        private ActorSystem actSystem;

        public AkkaDistributedDictionaryBase(AkkaDistributedDictConfig config)
        {
            if (config == null)
                throw new ArgumentException("Configuration must be specified.");

            this.Config = config;

            dictActors = new IActorRef[config.Nodes.Count];

            actSystem = ActorSystem.Create("Deployer", ConfigurationFactory.ParseString(@"
                akka {  
                    actor{
                        provider = ""Akka.Remote.RemoteActorRefProvider, Akka.Remote""                        
                    }
                    remote {
                        helios.tcp {
		                    port = 0
		                    hostname = localhost
                        }
                    }
                }"));

            int nodeIndx = 0;
            foreach (var node in this.Config.Nodes)
            {
                dictActors[nodeIndx] =
                  actSystem.ActorOf(Props.Create(() => new DictNodeActor())
                  .WithDeploy(Deploy.None.WithScope(new RemoteScope(Address.Parse(node)))), $"{nameof(DictNodeActor)}-{nodeIndx}");

                var result = dictActors[nodeIndx].Ask<int>(new DictNodeActor.CreateDictNodeMsg(), this.Config.ConnectionTimout).Result;
            }
        }

        /// <summary>
        /// Depending on usage (Key type) different mechanism can be used to partition keys.
        /// This method returns the index of the node, whish should hold specified key.
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        protected abstract int GetNodeIndexFromKey(TKey key);        

        public TValue this[TKey key]
        {
            get
            {
                var nodeIndex = GetNodeIndexFromKey(key);
                TValue val = this.dictActors[nodeIndex].Ask<TValue>("").Result;
                return val;
            }
            set
            {
                var nodeIndex = GetNodeIndexFromKey(key);
                var isSet = this.dictActors[nodeIndex].Ask<bool>("").Result;

                if (!isSet)
                    throw new ArgumentException("Cannot find the element with specified key!");
            }
        }

        private int getPartitionIndex(int elementIndx)
        {
            return elementIndx % this.dictList.Length;
        }

        public ICollection<TKey> Keys
        {
            get
            {
                List<TKey> keys = new List<TKey>();
                foreach (var item in this.dictList)
                {
                    foreach (var k in item.Keys)
                    {
                        keys.Add(k);
                    }
                }

                return keys;
            }
        }


        public ICollection<TValue> Values
        {
            get
            {
                List<TValue> keys = new List<TValue>();
                foreach (var item in this.dictList)
                {
                    foreach (var k in item.Values)
                    {
                        keys.Add(k);
                    }
                }

                return keys;
            }
        }


        public int Count
        {
            get
            {
                int cnt = 0;

                foreach (var item in this.dictList)
                {
                    cnt += item.Values.Count;
                }

                return cnt;
            }
        }

        public bool IsReadOnly => false;


        public void Add(TKey key, TValue value)
        {
            int partitionInd = getPartitionIndex(numElements++);
            this.dictList[partitionInd].Add(key, value);
        }

        public void Add(KeyValuePair<TKey, TValue> item)
        {
            int partitionInd = getPartitionIndex(++numElements);
            this.dictList[partitionInd].Add(item.Key, item.Value);
        }

        public void Clear()
        {
            foreach (var item in this.dictList)
            {
                item.Clear();
            }
        }

        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            for (int i = 0; i < this.dictList.Length; i++)
            {
                if (this.dictList[i].ContainsKey(item.Key))
                {
                    if (EqualityComparer<TValue>.Default.Equals(this.dictList[i][item.Key], item.Value))
                        return true;
                    else
                        return false;
                }
            }

            return false;
        }

        public bool ContainsKey(TKey key)
        {
            for (int i = 0; i < this.dictList.Length; i++)
            {
                if (this.dictList[i].ContainsKey(key))
                {
                    return true;
                }
            }

            return false;
        }

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            throw new NotImplementedException();
        }

        public bool Remove(TKey key)
        {
            for (int i = 0; i < this.dictList.Length; i++)
            {
                if (this.dictList[i].ContainsKey(key))
                {
                    return this.dictList[i].Remove(key);
                }
            }

            return false;
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            for (int i = 0; i < this.dictList.Length; i++)
            {
                if (this.dictList[i].ContainsKey(item.Key))
                {
                    return this.dictList[i].Remove(item.Key);
                }
            }

            return false;
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            for (int i = 0; i < this.dictList.Length; i++)
            {
                if (this.dictList[i].ContainsKey(key))
                {
                    value = this.dictList[i][key];
                    return true;
                }
            }

            value = default(TValue);
            return false;
        }


        IEnumerator IEnumerable.GetEnumerator()
        {
            return this;
        }

        #region Enumerators

        /// <summary>
        /// Current dictionary list in enemerator.
        /// </summary>
        private int currentDictIndex = -1;

        /// <summary>
        /// Current index in currentdictionary
        /// </summary>
        private int currentIndex = -1;

        public object Current => this.dictList[this.currentDictIndex].ElementAt(currentIndex);

        KeyValuePair<TKey, TValue> IEnumerator<KeyValuePair<TKey, TValue>>.Current => this.dictList[this.currentDictIndex].ElementAt(currentIndex);


        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return this;
        }


        public bool MoveNext()
        {
            if (this.currentIndex == -1)
                this.currentIndex = 0;

            if (this.currentDictIndex + 1 < this.dictList.Length)
            {
                this.currentDictIndex++;

                if (this.dictList[this.currentDictIndex].Count > 0 && this.dictList[this.currentDictIndex].Count > this.currentIndex)
                    return true;
                else
                    return false;
            }
            else
            {
                this.currentDictIndex = 0;

                if (this.currentIndex + 1 < this.dictList[this.currentDictIndex].Count)
                {
                    this.currentIndex++;
                    return true;
                }
                else
                    return false;
            }
        }


        public bool MoveNextOLD()
        {
            if (this.currentDictIndex == -1)
                this.currentDictIndex++;

            if (this.currentIndex + 1 < this.dictList[this.currentDictIndex].Count)
            {
                this.currentIndex++;
                return true;
            }
            else
            {
                if (this.currentDictIndex < this.dictList.Length)
                {
                    this.currentDictIndex++;

                    if (this.dictList[this.currentDictIndex].Count > 0)
                        return true;
                    else
                        return false;
                }
                else
                    return false;
            }
        }

        public void Reset()
        {
            this.currentDictIndex = -1;
            this.currentIndex = -1;
        }

        public void Dispose()
        {
            this.dictList = null;
        }
        #endregion
    }
}