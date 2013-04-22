﻿using System.Collections.Generic;
using Orient.Client.Protocol;
using Orient.Client.Protocol.Operations;

// syntax:
// INSERT INTO <Class>|cluster:<cluster>|index:<index> 
// [<cluster>](cluster) 
// [VALUES (<expression>[,]((<field>[,]*))*)]|[<field> = <expression>[,](SET)*]

namespace Orient.Client
{
    public class OSqlInsert
    {
        private SqlQuery _sqlQuery = new SqlQuery();
        private SqlQuery2 _sqlQuery2 = new SqlQuery2();
        private Connection _connection;

        public OSqlInsert()
        {
        }

        internal OSqlInsert(Connection connection)
        {
            _connection = connection;
        }

        public OSqlInsert Insert(ODocument document)
        {
            // check for OClassName shouldn't have be here since INTO clause might specify it

            _sqlQuery2.Insert(document);

            return this;
        }

        #region Into

        public OSqlInsert Into(string className)
        {
            _sqlQuery2.Class(className);

            return this;
        }

        public OSqlInsert Into<T>()
        {
            Into(typeof(T).Name);

            return this;
        }

        #endregion

        #region Cluster

        public OSqlInsert Cluster(string clusterName)
        {
            _sqlQuery2.Cluster(clusterName);

            return this;
        }

        public OSqlInsert Cluster<T>()
        {
            return Cluster(typeof(T).Name);
        }

        #endregion

        #region Set

        public OSqlInsert Set<T>(string fieldName, T fieldValue)
        {
            _sqlQuery2.Set<T>(fieldName, fieldValue);

            return this;
        }

        public OSqlInsert Set<T>(T obj)
        {
            _sqlQuery2.Set(obj);

            return this;
        }

        #endregion

        public ODocument Run()
        {
            CommandPayload payload = new CommandPayload();
            payload.Type = CommandPayloadType.Sql;
            payload.Text = ToString();
            payload.NonTextLimit = -1;
            payload.FetchPlan = "";
            payload.SerializedParams = new byte[] { 0 };

            Command operation = new Command();
            operation.OperationMode = OperationMode.Synchronous;
            operation.ClassType = CommandClassType.NonIdempotent;
            operation.CommandPayload = payload;

            OCommandResult result = new OCommandResult(_connection.ExecuteOperation<Command>(operation));

            return result.ToSingle();
        }

        public override string ToString()
        {
            return _sqlQuery2.ToString(QueryType.Insert);
        }
    }
}
