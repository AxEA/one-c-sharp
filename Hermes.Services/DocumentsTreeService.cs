﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Zhichkin.Hermes.Infrastructure;
using Zhichkin.Metadata.Model;
using Zhichkin.Metadata.Services;
using Zhichkin.ORM;

namespace Zhichkin.Hermes.Services
{
    public sealed class DocumentsTreeService
    {
        private const string CONST_ConnectionStringName = "TEST";
        private const string CONST_MetadataCatalogSettingName = "MetadataCatalog";
        private string connection_string;
        private string temporary_catalog;

        public DocumentsTreeService()
        {
            connection_string = ConfigurationManager.ConnectionStrings[CONST_ConnectionStringName].ConnectionString;
            temporary_catalog = ConfigurationManager.AppSettings[CONST_MetadataCatalogSettingName];
        }
        private void WriteToLog(string entry)
        {
            string path = Path.Combine(temporary_catalog, "log.txt");
            using (StreamWriter writer = new StreamWriter(path, true))
            {
                writer.WriteLine(entry);
                writer.Close();
            }
        }
        private string GetErrorText(Exception ex)
        {
            string errorText = string.Empty;
            Exception error = ex;
            while (error != null)
            {
                errorText += (errorText == string.Empty) ? error.Message : Environment.NewLine + error.Message;
                error = error.InnerException;
            }
            return errorText;
        }
        public Dictionary<string, object> Parameters { get; } = new Dictionary<string, object>();

        /// <summary>
        /// Процедура формирует дерево ссылок на дочерние объекты.
        /// </summary>
        /// <param name="node">
        /// Корневой узел данных, для объекта которого
        /// необходимо выполнить формирование дерева ссылок.
        /// </param>
        /// <returns></returns>
        public void BuildDocumentsTree(Entity entity, IProgress<MetadataTreeNode> progress)
        {
            if (entity == null) throw new ArgumentNullException("entity");

            MetadataTreeNode root = new MetadataTreeNode()
            {
                Name = entity.Name,
                MetadataInfo = entity
            };

            // 1. Построение предварительного дерева узлов данных
            string message = "START < BuildDocumentsTree > " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
            WriteToLog(message);
            Stopwatch sw = new Stopwatch();
            sw.Start();
            FillChildren(root);
            //RemoveZeroCountNodes(root);
            sw.Stop();
            message = "END < BuildDocumentsTree > " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)
                + " = " + sw.Elapsed.TotalSeconds.ToString() + " seconds";
            WriteToLog(message);

            // 2. Дополнение предварительного дерева недостающими табличными частями справчоников и документов
            //    для обеспечения регистрации внешних ссылок всего агрегата
            CompleteMetadataTreeNode(root);

            // 3. Создание таблицы регистрации ссылок
            CreateReferencesRegisterTable();

            sw = new Stopwatch();
            sw.Start();
            message = "START < RegisterNodeReferences > " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
            WriteToLog(message);

            // 4. Регистрация и подсчёт существующих ссылок согласно построенному дереву и его настройкам
            RegisterAllEntitiesForExchange(root);
            
            sw.Stop();
            message = "END < RegisterNodeReferences > " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)
                + " = " + sw.Elapsed.TotalSeconds.ToString() + " seconds";
            WriteToLog(message);

            //RemoveZeroCountNodes(root);

            progress.Report(root);
        }
        private void RemoveZeroCountNodes(IMetadataTreeNode root)
        {
            int index = 0;
            while (index < root.Children.Count)
            {
                IMetadataTreeNode node = root.Children[index];
                if (node.Count == 0)
                {
                    root.Children.RemoveAt(index);
                }
                else
                {
                    RemoveZeroCountNodes(node);
                    index++;
                }
            }
        }
        private void FillChildren(MetadataTreeNode parent)
        {
            if (!(parent.MetadataInfo is IEntityInfo)) return;

            IPersistentContext context = MetadataPersistentContext.Current;
            using (SqlConnection connection = new SqlConnection(context.ConnectionString))
            using (SqlCommand command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandType = CommandType.StoredProcedure;
                command.CommandText = "[dbo].[get_metadata_tree_node_children]";
                command.Parameters.AddWithValue("entity", parent.MetadataInfo.Identity);
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    Guid current_ns_key = Guid.Empty;
                    Namespace current_ns = null;
                    MetadataTreeNode current_ns_node = null;

                    Guid current_entity_key = Guid.Empty;
                    Entity current_entity = null;
                    MetadataTreeNode current_entity_node = null;

                    Guid current_nested_key = Guid.Empty;
                    Entity current_nested = null;
                    MetadataTreeNode current_nested_node = null;

                    Property current_property = null;

                    while (reader.Read())
                    {
                        Guid key = (Guid)reader["NamespaceKey"];
                        if (current_ns_key != key)
                        {
                            current_ns_key = key;
                            current_ns = context.Factory.New<Namespace>(key);
                            current_ns_node = new MetadataTreeNode()
                            {
                                Name = current_ns.Name,
                                Parent = parent,
                                MetadataInfo = current_ns
                            };
                            parent.Children.Add(current_ns_node);
                        }

                        key = (Guid)reader["EntityKey"];
                        if (current_entity_key != key)
                        {
                            current_entity_key = key;
                            current_entity = context.Factory.New<Entity>(key);
                            current_entity_node = new MetadataTreeNode()
                            {
                                Name = current_entity.Name,
                                Parent = current_ns_node,
                                MetadataInfo = current_entity,
                                Filter = new BooleanExpression()
                                {
                                    ExpressionType = BooleanExpressionType.OR
                                }
                            };
                            current_ns_node.Children.Add(current_entity_node);
                        }

                        key = (Guid)reader["NestedEntityKey"];
                        if (current_nested_key != key)
                        {
                            if (key == Guid.Empty)
                            {
                                current_nested_key = Guid.Empty;
                                current_nested = null;
                                current_nested_node = null;
                            }
                            else
                            {
                                current_nested_key = key;
                                current_nested = context.Factory.New<Entity>(key);
                                current_nested_node = new MetadataTreeNode()
                                {
                                    Name = current_nested.Name,
                                    Parent = current_entity_node,
                                    MetadataInfo = current_nested,
                                    Filter = new BooleanExpression()
                                    {
                                        ExpressionType = BooleanExpressionType.OR
                                    }
                                };
                                current_entity_node.Children.Add(current_nested_node);
                            }
                        }

                        key = (Guid)reader["PropertyKey"];
                        current_property = context.Factory.New<Property>(key);
                        PropertyExpression px = new PropertyExpression()
                        {
                            Name = current_property.Name,
                            PropertyInfo = current_property
                        };
                        ComparisonExpression ce = new ComparisonExpression()
                        {
                            Name = BooleanExpressionType.Equal.ToString(),
                            ExpressionType = BooleanExpressionType.Equal,
                            LeftExpression = px,
                            RightExpression = null
                        };

                        if (current_nested_node == null)
                        {
                            ((BooleanExpression)current_entity_node.Filter).Conditions.Add(ce);
                            //CountDocuments(current_entity_node, current_property);
                        }
                        else
                        {
                            ((BooleanExpression)current_nested_node.Filter).Conditions.Add(ce);
                            //CountDocuments(current_nested_node, current_property);
                        }
                    }
                }
            }
        }
        private void CompleteMetadataTreeNode(MetadataTreeNode node)
        {
            Entity entity = node.MetadataInfo as Entity;
            if (entity == null)
            {
                // Namespace's child nodes
                foreach (MetadataTreeNode child in node.Children)
                {
                    CompleteMetadataTreeNode(child);
                }
            }
            else // different kind of entities
            {
                if (entity.Owner != null) { return; }

                string namespaceName = entity.Namespace.Name;

                if (namespaceName == "Справочник" || namespaceName == "Документ")
                {
                    foreach (Entity nestedEntity in entity.NestedEntities)
                    {
                        IMetadataTreeNode child = node.Children.Where((c) => c.Name == nestedEntity.Name).FirstOrDefault();
                        if (child == null)
                        {
                            Property filterProperty = nestedEntity.Properties.Where((p) => p.Name == "Ссылка").First();
                            PropertyExpression px = new PropertyExpression()
                            {
                                Name = "Ссылка",
                                PropertyInfo = filterProperty
                            };
                            ComparisonExpression ce = new ComparisonExpression()
                            {
                                Name = BooleanExpressionType.Equal.ToString(),
                                ExpressionType = BooleanExpressionType.Equal,
                                LeftExpression = px,
                                RightExpression = null
                            };
                            child = new MetadataTreeNode()
                            {
                                Name = nestedEntity.Name,
                                Parent = node,
                                MetadataInfo = nestedEntity,
                                Filter = new BooleanExpression()
                                {
                                    ExpressionType = BooleanExpressionType.OR
                                }
                            };
                            ((BooleanExpression)child.Filter).Conditions.Add(ce);
                            node.Children.Add(child);
                        }
                    }

                    foreach (MetadataTreeNode child in node.Children)
                    {
                        CompleteMetadataTreeNode(child);
                    }
                }
            }
        }
        private void CountDocuments(MetadataTreeNode node, IPropertyInfo property)
        {
            if (property.Fields == null || property.Fields.Count == 0)
            {
                return;
            }
            foreach (IFieldInfo field in property.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.Name)) return;
            }

            MetadataTreeNode root = node;
            while (root.Parent != null) { root = (MetadataTreeNode)root.Parent; }
            if (root.Keys == null) { root.Keys = GetRootKeys(root); }
            if (root.Keys.Count == 0) return;
            
            StringBuilder queryText = new StringBuilder();
            queryText.Append("DECLARE @OrdersXML nvarchar(max) = '");
            queryText.Append("<list>");
            foreach (Guid key in root.Keys)
            {
                queryText.AppendFormat("<item key=\"{0}\"/>", key.ToString());
            }
            queryText.AppendLine("</list>';");
            queryText.AppendLine("DECLARE @xml xml = CAST(@OrdersXML AS xml);");
            queryText.AppendLine("DECLARE @Orders TABLE([key] binary(16) NOT NULL);");
            queryText.AppendLine("INSERT @Orders([key])");
            queryText.AppendLine("SELECT");
            queryText.AppendLine("list.[item].value('@key', 'uniqueidentifier')");
            queryText.AppendLine("FROM @xml.nodes('list/item') AS list([item]);");
            
            string table_name = ((Entity)property.Entity).MainTable.Name;

            string where_filter = "";
            if (property.Fields.Count == 1)
            {
                where_filter = string.Format("S.[{0}] = T.[key]", property.Fields[0].Name);
            }
            else
            {
                string value_name = "";
                string locator_name = "";
                string type_code_name = "";
                int type_code = ((Entity)root.MetadataInfo).Code;
                foreach (IFieldInfo field in property.Fields)
                {
                    switch (field.Purpose)
                    {
                        case FieldPurpose.Object: { value_name = field.Name; break; }
                        case FieldPurpose.Locator: { locator_name = field.Name; break; }
                        case FieldPurpose.TypeCode: { type_code_name = field.Name; break; }
                    }
                }
                if (locator_name == string.Empty)
                {
                    where_filter = string.Format("S.[{0}] = CAST({1} AS binary(4)) AND S.[{2}] = T.[key]",
                        type_code_name, type_code, value_name);
                }
                else
                {
                    where_filter = string.Format("S.[{0}] = 0x08 AND S.[{1}] = CAST({2} AS binary(4)) AND S.[{3}] = T.[key]",
                        locator_name, type_code_name, type_code, value_name);
                }
            }
            string select_statement = string.Format("SELECT COUNT(*) FROM [{0}] AS S INNER JOIN @Orders AS T ON {1};", table_name, where_filter);
            queryText.Append(select_statement);
            WriteToLog(select_statement + Environment.NewLine);

            using (SqlConnection connection = new SqlConnection(connection_string))
            using (SqlCommand command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandType = CommandType.Text;
                command.CommandText = queryText.ToString();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int count = (int)reader[0];
                        node.Count += count;
                        MetadataTreeNode next_node_up = (MetadataTreeNode)node.Parent;
                        while (next_node_up != null && next_node_up != root)
                        {
                            next_node_up.Count += count;
                            next_node_up = (MetadataTreeNode)next_node_up.Parent;
                        }
                    }
                }
            }
        }
        private List<Guid> GetRootKeys(MetadataTreeNode root)
        {
            DateTime period = (DateTime)this.Parameters["Period"];
            Guid department = (Guid)this.Parameters["Department"];
            
            List<Guid> keys = new List<Guid>();

            string table_name = ((Entity)root.MetadataInfo).MainTable.Name;

            DateTime start_of_period = new DateTime(period.Year, period.Month, period.Day, 0, 0, 0, 0);
            start_of_period = start_of_period.AddYears(2000); // fuck 1C !!!

            string filterName = GetDepartmentFieldName((Entity)root.MetadataInfo);

            StringBuilder query = new StringBuilder();
            query.Append("SELECT [_IDRRef] FROM [" + table_name + "] WHERE [_Date_Time] >= @_Date_Time ");
            query.Append("AND [" + filterName + "] = @Department;");

            using (SqlConnection connection = new SqlConnection(connection_string))
            using (SqlCommand command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandType = CommandType.Text;
                command.CommandText = query.ToString();
                command.Parameters.AddWithValue("_Date_Time", start_of_period);
                command.Parameters.AddWithValue("Department", department.ToByteArray());
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        Guid key = new Guid((byte[])reader[0]);
                        keys.Add(key);
                    }
                }
            }
            root.Count = keys.Count;

            WriteToLog("Priod = " + start_of_period.ToString("dd.MM.yyyy HH:mm:ss.ffff", CultureInfo.InvariantCulture));
            WriteToLog(query.ToString());
            WriteToLog("Count = " + root.Count.ToString() + Environment.NewLine);

            return keys;
        }

        /// <summary>
        /// Регистрация корневых и внешних ссылок для обмена
        /// </summary>
        /// <param name="node">
        /// Корневой узел данных
        /// </param>
        /// <returns>
        /// Список зарегистрированных ссылок по типам объектов и их количество
        /// </returns>
        public List<MetadataTreeNode> RegisterEntitiesForExchange(MetadataTreeNode root)
        {
            return GetExchangeInfo(((Entity)root.MetadataInfo).InfoBase);
        }
        private void RegisterAllEntitiesForExchange(MetadataTreeNode node)
        {
            Entity entity = node.MetadataInfo as Entity;
            if (entity == null)
            {
                // Namespace's child nodes
                foreach (MetadataTreeNode child in node.Children)
                {
                    RegisterAllEntitiesForExchange(child);
                }
            }
            else // different kind of entities
            {
                string namespaceName = entity.Namespace.Name;
                if (entity.Owner == null)
                {
                    namespaceName = entity.Namespace.Name;
                }
                else // nested entity
                {
                    namespaceName = entity.Owner.Namespace.Name;
                }

                if (namespaceName == "Перечисление"
                    || namespaceName == "ПланСчетов"
                    || namespaceName == "ПланВидовХарактеристик"
                    || namespaceName == "РегистрБухгалтерии") { return; }

                if (namespaceName == "Справочник" || namespaceName == "Документ")
                {
                    // including nested entities
                    RegisterNodeReferences(node);
                }
                else
                {
                    // TODO : регистрация на выгрузку для РегистрСведений РегистрНакопления РегистрБухгалтерии ?
                }

                RegisterNodeForeignReferences(node);

                foreach (MetadataTreeNode child in node.Children)
                {
                    RegisterAllEntitiesForExchange(child);
                }
            }
        }
        private List<MetadataTreeNode> GetExchangeInfo(InfoBase infoBase)
        {
            List<MetadataTreeNode> result = new List<MetadataTreeNode>();
            List<dynamic> list = SelectExchangeInfo(infoBase);
            foreach (dynamic item in list)
            {
                MetadataTreeNode node = new MetadataTreeNode()
                {
                    Name = item.Entity.Name,
                    Count = item.Count,
                    MetadataInfo = item.Entity
                };
                result.Add(node);
            }
            return result;
        }

        /// <summary>
        /// Процедура отбирает объекты узла данных,
        /// по настроенному для него фильтру,
        /// а затем регистрирует ссылки этих объектов для обмена.
        /// </summary>
        /// <param name="node">
        /// Узел данных, для объектов которого
        /// необходимо выполнить регистрацию
        /// ссылок для обмена.
        /// </param>
        /// <returns></returns>
        
        private List<Guid> GetNodeKeys(MetadataTreeNode node)
        {
            if (node == null) { throw new ArgumentNullException("node"); }
            if (node.Keys != null) { return node.Keys; }

            MetadataTreeNode parent = GetParentNode(node);
            if (parent == null)
            {
                node.Keys = GetRootKeys(node);
                return node.Keys;
            }

            List<Guid> parent_keys = GetNodeKeys(parent);

            node.Keys = new List<Guid>();

            Entity entity = parent.MetadataInfo as Entity;
            Entity source = node.MetadataInfo as Entity;
            List<Property> filters = GetFilterProperties(node);

            StringBuilder query = new StringBuilder();
            query.AppendLine(BuildKeysTableQueryScript(parent_keys));
            query.Append(BuildSelectParentKeysScript(source, filters, entity));

            using (SqlConnection connection = new SqlConnection(connection_string))
            using (SqlCommand command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandType = CommandType.Text;
                command.CommandText = query.ToString();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        node.Keys.Add(new Guid((byte[])reader[0]));
                    }
                }
            }

            return node.Keys;
        }
        
        private List<dynamic> SelectExchangeInfo(InfoBase infoBase)
        {
            List<dynamic> list = new List<dynamic>();

            StringBuilder query = new StringBuilder();
            query.Append("SELECT [ENTITY], COUNT([OBJ_REF]) FROM ");
            query.Append(GetReferencesRegisterTableName());
            query.Append(" GROUP BY [ENTITY] ORDER BY [ENTITY];");

            using (SqlConnection connection = new SqlConnection(connection_string))
            using (SqlCommand command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandType = CommandType.Text;
                command.CommandText = query.ToString();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    IPersistentContext context = MetadataPersistentContext.Current;
                    MetadataService service = new MetadataService();
                    while (reader.Read())
                    {
                        dynamic item = new ExpandoObject();
                        Entity entity = service.GetEntityInfo(infoBase, (int)reader[0]);
                        int count = (int)reader[1];
                        ((IDictionary<string, object>)item).Add("Entity", entity);
                        ((IDictionary<string, object>)item).Add("Count", count);
                        list.Add(item);
                    }
                }
            }
            return list;
        }
        
        private MetadataTreeNode GetParentNode(MetadataTreeNode node)
        {
            IMetadataTreeNode parent = node.Parent;
            while (parent != null)
            {
                if (!(parent.MetadataInfo is INamespaceInfo))
                {
                    break;
                }
                parent = parent.Parent;
            }
            return (parent as MetadataTreeNode);
        }
        
        private List<Guid> GetParentKeys(MetadataTreeNode node)
        {
            if (node == null) { throw new ArgumentNullException("node"); }

            MetadataTreeNode parent = GetParentNode(node);
            if (parent == null) { return null; }
            if (parent.Keys != null) { return parent.Keys; }
            if (parent.Parent == null)
            {
                parent.Keys = GetRootKeys(parent);
                return parent.Keys;
            }

            List<Guid> parent_keys = null;
            while (parent_keys == null)
            {
                parent_keys = GetParentKeys(parent);
            }

            List<Guid> keys = new List<Guid>();

            Entity entity = parent.MetadataInfo as Entity;
            Entity source = node.MetadataInfo as Entity;
            List<Property> filters = GetFilterProperties(node);
            
            StringBuilder query = new StringBuilder();
            query.AppendLine(BuildKeysTableQueryScript(parent_keys));
            query.Append(BuildSelectParentKeysScript(source, filters, entity));

            using (SqlConnection connection = new SqlConnection(connection_string))
            using (SqlCommand command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandType = CommandType.Text;
                command.CommandText = query.ToString();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        keys.Add(new Guid((byte[])reader[0]));
                    }
                }
            }

            return keys;
        }
        private string BuildKeysTableQueryScript(List<Guid> keys)
        {
            StringBuilder script = new StringBuilder();
            script.Append("DECLARE @KeysListXML nvarchar(max) = '<list>");
            foreach (Guid key in keys)
            {
                script.AppendFormat("<item key=\"{0}\"/>", key.ToString());
            }
            script.AppendLine("</list>';");
            script.AppendLine("DECLARE @xml xml = CAST(@KeysListXML AS xml);");
            script.AppendLine("DECLARE @KeysTable TABLE([key] binary(16) PRIMARY KEY);");
            script.AppendLine("INSERT @KeysTable([key])");
            script.AppendLine("SELECT");
            script.AppendLine("list.[item].value('@key', 'uniqueidentifier')");
            script.AppendLine("FROM @xml.nodes('list/item') AS list([item]);");
            return script.ToString();
        }
        private string BuildSelectParentKeysScript(Entity source, List<Property> filters, Entity parent)
        {
            StringBuilder script = new StringBuilder();
            for (int i = 0; i < filters.Count; i++)
            {
                script.AppendLine(BuildSelectParentKeysForOnePropertyScript(source, filters[i], parent));
                if (filters.Count > 1 && i < filters.Count - 1)
                {
                    script.AppendLine("UNION");
                }
            }
            script.Append(";");
            return script.ToString();
        }
        private string BuildSelectParentKeysForOnePropertyScript(Entity source, Property filter, Entity parent)
        {
            StringBuilder script = new StringBuilder();
            script.Append("SELECT [_IDRRef] FROM [");
            script.Append(source.MainTable.Name);
            script.Append("] AS T INNER JOIN @KeysTable AS K ON ");
            script.Append(BuildFilterQueryScript(filter, "T", parent.Code));
            script.Append(";");
            return script.ToString();
        }
        private string BuildFilterQueryScript(Property property, string tableAlias, int typeCode)
        {
            StringBuilder script = new StringBuilder();

            if (property.Fields.Count == 1)
            {
                script.Append(string.Format("{0}.[{1}] = K.[key]", tableAlias, property.Fields[0].Name));
            }
            else
            {
                string object_field = string.Empty;
                string locator_field = string.Empty;
                string type_code_field = string.Empty;
                foreach (IFieldInfo field in property.Fields)
                {
                    switch (field.Purpose)
                    {
                        case FieldPurpose.Object: { object_field = field.Name; break; }
                        case FieldPurpose.Locator: { locator_field = field.Name; break; }
                        case FieldPurpose.TypeCode: { type_code_field = field.Name; break; }
                    }
                }
                if (locator_field == string.Empty)
                {
                    script.Append(string.Format(
                        "{0}.[{1}] = CAST({2} AS binary(4)) AND {0}.[{3}] = K.[key]",
                        tableAlias, type_code_field, typeCode, object_field));
                }
                else
                {
                    script.Append(string.Format(
                        "{0}.[{1}] = 0x08 AND {0}.[{2}] = CAST({3} AS binary(4)) AND {0}.[{4}] = K.[key]",
                        tableAlias, locator_field, type_code_field, typeCode, object_field));
                }
            }

            return script.ToString();
        }

        private string BuildSelectForeignKeysScript(Entity parent, Entity source, List<Property> filters, List<Property> foreiners)
        {
            if (parent == null)
            {
                return SelectForeignKeysFromRootScript(source, foreiners);
            }
            else
            {
                return SelectForeignKeysFromChildScript(parent, source, filters, foreiners);
            }
        }
        private string SelectForeignKeysFromRootScript(Entity source, List<Property> foreiners)
        {
            string filterName = GetDepartmentFieldName(source);
            string sourceTable = string.Format("[{0}].[dbo].[{1}]", source.InfoBase.Database, source.MainTable.Name);

            StringBuilder script = new StringBuilder();
            string fields = SelectFieldsScript(foreiners);
            script.AppendLine(string.Format("WITH CTE ({0}) AS", fields));
            script.AppendLine("(");
            script.Append(string.Format("SELECT {0} FROM {1} WHERE [_Date_Time] >= @period", fields, sourceTable));
            script.Append(" AND [" + filterName + "] = @branch");
            script.AppendLine(")");
            script.Append(MergeForeignKeysFromEntityToExchangeTableScript(foreiners));
            script.Append(";");
            return script.ToString();
        }
        private string SelectFieldsScript(List<Property> properties)
        {
            StringBuilder script = new StringBuilder();
            foreach (Property property in properties)
            {
                if (script.Length > 0) { script.Append(", "); }
                script.Append(FieldsForOnePropertyScript(property));
            }
            return script.ToString();
        }
        private string FieldsForOnePropertyScript(Property property)
        {
            StringBuilder script = new StringBuilder();
            if (property.Fields.Count == 1)
            {
                script.Append("[");
                script.Append(property.Fields[0].Name);
                script.Append("]");
            }
            else
            {
                foreach (Field field in property.Fields)
                {
                    if (field.Purpose == FieldPurpose.Object || field.Purpose == FieldPurpose.Locator || field.Purpose == FieldPurpose.TypeCode)
                    {
                        if (script.Length > 0) { script.Append(", "); }
                        script.Append("[");
                        script.Append(field.Name);
                        script.Append("]");
                    }
                }
            }
            return script.ToString();
        }
        private string SelectForeignKeysFromEntityScript(List<Property> foreiners)
        {
            StringBuilder script = new StringBuilder();
            for (int i = 0; i < foreiners.Count; i++)
            {
                script.AppendLine(SelectForeignKeysForOnePropertyScript(foreiners[i]));
                if (foreiners.Count > 1 && i < foreiners.Count - 1)
                {
                    script.AppendLine("UNION");
                }
            }
            return script.ToString();
        }
        private string SelectForeignKeysForOnePropertyScript(Property property)
        {
            StringBuilder script = new StringBuilder();

            if (property.Fields.Count == 1)
            {
                script.Append("SELECT ");
                script.Append(property.Relations[0].Entity.Code.ToString());
                script.Append(" AS [TYPE_CODE], [");
                script.Append(property.Fields[0].Name);
                script.Append("] AS [FK_VALUE] FROM CTE WHERE [");
                script.Append(property.Fields[0].Name);
                script.Append("] > 0x00000000000000000000000000000000");
            }
            else // составной тип данных
            {
                string object_field = string.Empty;
                string locator_field = string.Empty;
                string type_code_field = string.Empty;
                foreach (Field field in property.Fields)
                {
                    switch (field.Purpose)
                    {
                        case FieldPurpose.Object: { object_field = field.Name; break; }
                        case FieldPurpose.Locator: { locator_field = field.Name; break; }
                        case FieldPurpose.TypeCode: { type_code_field = field.Name; break; }
                    }
                }
                if (type_code_field == string.Empty) // в составном типе есть только один ссылочный тип
                {
                    script.Append("SELECT ");
                    foreach (Relation relation in property.Relations)
                    {
                        if (relation.Entity.Code > 0) // пользовательский тип данных, в данном случае только ссылочный может быть
                        {
                            script.Append(relation.Entity.Code.ToString());
                            break;
                        }
                    }
                    script.Append(" AS [TYPE_CODE], [");
                    script.Append(object_field);
                    script.Append("] AS [FK_VALUE] FROM CTE WHERE [");
                    script.Append(object_field);
                    script.Append("] > 0x00000000000000000000000000000000");
                }
                else
                {
                    script.Append("SELECT CAST([");
                    script.Append(type_code_field);
                    script.Append("] AS int) AS [TYPE_CODE], [");
                    script.Append(object_field);
                    script.Append("] AS [FK_VALUE] FROM CTE WHERE [");
                    script.Append(type_code_field);
                    script.Append("] > 0x00000000 AND [");
                    script.Append(object_field);
                    script.Append("] > 0x00000000000000000000000000000000");
                }
                if (locator_field != string.Empty)
                {
                    script.Append(" AND [");
                    script.Append(locator_field);
                    script.Append("] = 0x08");
                }
            }

            return script.ToString();
        }
        private string SelectForeignKeysFromChildScript(Entity parent, Entity source, List<Property> filters, List<Property> foreiners)
        {
            if (parent == null) throw new ArgumentNullException("parent");

            StringBuilder script = new StringBuilder();
            script.AppendLine("WITH CTE (");
            script.Append(SelectFieldsScript(foreiners));
            script.AppendLine(") AS");
            script.AppendLine("(");
            script.AppendLine(SelectNodeForeignReferencesScript(parent, source, filters, foreiners));
            script.AppendLine(")");
            script.Append(MergeForeignKeysFromEntityToExchangeTableScript(foreiners));
            return script.ToString();
        }
        private string SelectNodeForeignReferencesScript(Entity parent, Entity source, List<Property> filters, List<Property> foreiners)
        {
            StringBuilder script = new StringBuilder();
            for (int i = 0; i < filters.Count; i++)
            {
                script.AppendLine(SelectNodeForeignReferencesByOneFilterScript(parent, source, filters[i], foreiners));
                if (filters.Count > 1 && i < filters.Count - 1)
                {
                    script.AppendLine("UNION ALL");
                }
            }
            return script.ToString();
        }
        private string SelectNodeForeignReferencesByOneFilterScript(Entity parent, Entity source, Property filter, List<Property> foreiners)
        {
            string sourceTable = string.Format("[{0}].[dbo].[{1}]", source.InfoBase.Database, source.MainTable.Name);

            StringBuilder script = new StringBuilder();
            script.Append("SELECT ");
            script.Append(SelectFieldsScript(foreiners));
            script.Append(" FROM ");
            script.Append(sourceTable);
            script.Append(" AS S INNER JOIN ");
            script.Append(GetReferencesRegisterTableName());
            script.AppendLine(" AS T ON");
            script.Append("T.[NODE] = @parentNode AND T.[ENTITY] = @parentEntity AND ");
            script.Append(NodeReferencesFilterScript(filter, parent, "S", "T"));
            return script.ToString();
        }
        private string MergeForeignKeysFromEntityToExchangeTableScript(List<Property> foreiners)
        {
            StringBuilder script = new StringBuilder();
            script.Append("MERGE ");
            script.Append(GetReferencesRegisterTableName());
            script.AppendLine(" AS target");
            script.AppendLine("USING");
            script.AppendLine("(");
            script.AppendLine(SelectForeignKeysFromEntityScript(foreiners));
            script.AppendLine(") AS source(TYPE_CODE, FK_VALUE)");
            script.AppendLine("ON (target.[NODE] = @sourceNode AND target.[ENTITY] = source.TYPE_CODE AND target.[OBJ_REF] = source.FK_VALUE)");
            script.AppendLine("WHEN NOT MATCHED THEN");
            script.AppendLine("INSERT ([NODE], [ENTITY], [OBJ_REF]) VALUES (@sourceNode, source.TYPE_CODE, source.FK_VALUE);");
            return script.ToString();
        }

        //
        private string GetDepartmentFieldName(Entity entity)
        {
            return entity.Properties.Where((p) => p.Name == "Филиал").First().Fields[0].Name;
        }
        //

        public void SendDataToTargetInfoBase()
        {
            InfoBase source = (InfoBase)this.Parameters["SourceInfoBase"];
            InfoBase target = (InfoBase)this.Parameters["TargetInfoBase"];

            List<Entity> list = GetEnitiesToExport(source);
            if (list.Count == 0) return;

            CreateTypeCodesCorrespondenceFucntion(list, target);

            MetadataService service = new MetadataService();
            foreach (Entity sourceEntity in list)
            {
                if (sourceEntity.Namespace.Name == "Документ" || sourceEntity.Namespace.Name == "Справочник")
                {
                    Entity targetEntity = service.GetEntityInfo(target, sourceEntity.Namespace.Name, sourceEntity.Name);
                    if (targetEntity == null)
                    {
                        WriteToLog("Target entity not found: " + target.Name + ", " + sourceEntity.Namespace.Name + ", " + sourceEntity.Name);
                        continue;
                    }

                    int rowsAffected = SendEntityToTargetInfoBase(source, target, sourceEntity, targetEntity);
                    foreach (Entity nestedSource in sourceEntity.NestedEntities)
                    {
                        Entity nestedTarget = targetEntity.NestedEntities.Where((e) => e.Name == nestedSource.Name).FirstOrDefault();
                        if (nestedTarget == null) continue;
                        rowsAffected = SendEntityToTargetInfoBase(source, target, nestedSource, nestedTarget);
                    }
                }
            }

            // TODO: clear exchange table
        }
        private List<Entity> GetEnitiesToExport(InfoBase source)
        {
            List<Entity> list = new List<Entity>();

            StringBuilder query = new StringBuilder();
            query.Append("SELECT DISTINCT [ENTITY] FROM ");
            query.Append(GetReferencesRegisterTableName());
            query.Append(" ORDER BY [ENTITY];");

            MetadataService service = new MetadataService();
            using (SqlConnection connection = new SqlConnection(connection_string))
            using (SqlCommand command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandType = CommandType.Text;
                command.CommandText = query.ToString();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        Entity entity = service.GetEntityInfo(source, reader.GetInt32(0));
                        list.Add(entity);
                    }
                }
            }
            return list;
        }
        private int SendEntityToTargetInfoBase(InfoBase source, InfoBase target, Entity sourceEntity, Entity targetEntity)
        {
            int rowsAffected = 0;
            string query = MergeSourceWithTargetScript(source, sourceEntity, target, targetEntity);
            IPersistentContext context = MetadataPersistentContext.Current;
            using (SqlConnection connection = new SqlConnection(context.ConnectionString))
            using (SqlCommand command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandType = CommandType.Text;
                command.CommandText = query;
                try
                {
                    rowsAffected = command.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    WriteToLog("------------------------------");
                    WriteToLog("< SendEntityToTargetInfoBase >");
                    WriteToLog("Source: " + sourceEntity.Namespace.Name + "." + ((sourceEntity.Owner == null) ? "" : sourceEntity.Owner.Name + ".") + sourceEntity.Name);
                    WriteToLog("Target: " + targetEntity.Namespace.Name + "." + ((targetEntity.Owner == null) ? "" : targetEntity.Owner.Name + ".") + targetEntity.Name);
                    WriteToLog(query);
                    WriteToLog(GetErrorText(ex));
                    WriteToLog(ex.StackTrace);
                    WriteToLog(Environment.NewLine);
                    throw;
                }
            }

            WriteToLog("------------------------------");
            WriteToLog("< SendEntityToTargetInfoBase >");
            WriteToLog("Source: " + sourceEntity.Namespace.Name + "." + ((sourceEntity.Owner == null) ? "" : sourceEntity.Owner.Name + ".") + sourceEntity.Name);
            WriteToLog("Target: " + targetEntity.Namespace.Name + "." + ((targetEntity.Owner == null) ? "" : targetEntity.Owner.Name + ".") + targetEntity.Name);
            WriteToLog(query);
            WriteToLog("Rows affected = " + rowsAffected.ToString() + Environment.NewLine);

            return rowsAffected;
        }
        private string AllFieldsScript(Entity entity, string alias)
        {
            StringBuilder script = new StringBuilder();
            foreach (Property property in entity.Properties)
            {
                foreach (Field field in property.Fields)
                {
                    // TODO : add to Field class IsAutoGenerated property !!!
                    if (field.TypeName == "timestamp" || field.TypeName == "version") continue;
                    if (script.Length > 0) { script.Append(", "); }
                    if (alias != string.Empty)
                    {
                        script.Append(alias);
                        script.Append(".");
                    }
                    script.Append("[");
                    script.Append(field.Name);
                    script.Append("]");
                }
            }
            return script.ToString();
        }
        private string InsertTargetValuesScript(Entity entity, string alias)
        {
            string functionName = GetTypeCodesCorrespondenceFucntionName();
            StringBuilder script = new StringBuilder();
            foreach (Property property in entity.Properties)
            {
                foreach (Field field in property.Fields)
                {
                    // TODO : add to Field class IsAutoGenerated property !!!
                    if (field.TypeName == "timestamp" || field.TypeName == "version") continue;
                    if (script.Length > 0) { script.Append(", "); }
                    if (field.Purpose == FieldPurpose.TypeCode)
                    {
                        script.Append(functionName);
                        script.Append("(");
                    }
                    if (alias != string.Empty)
                    {
                        script.Append(alias);
                        script.Append(".");
                    }
                    script.Append("[");
                    script.Append(field.Name);
                    script.Append("]");
                    if (field.Purpose == FieldPurpose.TypeCode)
                    {
                        script.Append(")");
                    }
                }
            }
            return script.ToString();
        }
        private string SelectSourceEntityScript(InfoBase source, Entity entity)
        {
            StringBuilder script = new StringBuilder();
            script.Append("SELECT ");
            script.Append(AllFieldsScript(entity, string.Empty));
            script.Append(" FROM [");
            script.Append(source.Database);
            script.Append("].[dbo].[");
            script.Append(entity.MainTable.Name);
            script.Append("] AS E INNER JOIN ");
            script.Append("(SELECT DISTINCT [ENTITY], [OBJ_REF] FROM " + GetReferencesRegisterTableName() + ")");
            script.Append(" AS T ON E.[");
            script.Append(GetReferenceFieldName(entity));
            script.Append("] = T.[OBJ_REF] AND T.[ENTITY] = ");
            script.Append(GetReferenceTypeCode(entity).ToString());
            return script.ToString();
        }
        private string InsertTargetEntityScript(InfoBase target, Entity targetEntity, string alias, Entity sourceEntity)
        {
            StringBuilder script = new StringBuilder();
            script.Append("INSERT ");
            script.Append("(");
            script.Append(AllFieldsScript(targetEntity, string.Empty));
            script.AppendLine(")");
            script.Append("VALUES (");
            script.Append(InsertTargetValuesScript(sourceEntity, alias));
            script.Append(")");
            return script.ToString();
        }
        private string UpdateTargetEntityScript(InfoBase target, Entity targetEntity, string alias, Entity sourceEntity)
        {
            StringBuilder script = new StringBuilder();
            script.AppendLine("UPDATE SET");
            script.Append(UpdateTargetValuesScript(targetEntity, sourceEntity, alias));
            return script.ToString();
        }
        private string UpdateTargetValuesScript(Entity targetEntity, Entity sourceEntity, string alias)
        {
            string functionName = GetTypeCodesCorrespondenceFucntionName();
            StringBuilder script = new StringBuilder();
            foreach (Property targetProperty in targetEntity.Properties)
            {
                Property sourceProperty = sourceEntity.Properties.Where((p) => p.Name == targetProperty.Name).First();

                foreach (Field targetField in targetProperty.Fields)
                {
                    // TODO : add to Field class IsAutoGenerated property !!!
                    if (targetField.TypeName == "timestamp" || targetField.TypeName == "version") continue;

                    // TODO : filter out primary key fields - they are not needed by update statement !

                    Field sourceField = sourceProperty.Fields.Where((f) => f.Purpose == targetField.Purpose).First();

                    if (script.Length > 0) { script.Append(", "); }
                    script.Append("[");
                    script.Append(targetField.Name);
                    script.Append("] = ");
                    if (targetField.Purpose == FieldPurpose.TypeCode)
                    {
                        script.Append(functionName);
                        script.Append("(");
                    }
                    if (alias != string.Empty)
                    {
                        script.Append(alias);
                        script.Append(".");
                    }
                    script.Append("[");
                    script.Append(sourceField.Name);
                    script.Append("]");
                    if (targetField.Purpose == FieldPurpose.TypeCode)
                    {
                        script.Append(")");
                    }
                }
            }
            return script.ToString();
        }
        private string MergeSourceWithTargetScript(InfoBase source, Entity sourceEntity, InfoBase target, Entity targetEntity)
        {
            StringBuilder script = new StringBuilder();
            script.Append("MERGE [");
            script.Append(target.Database);
            script.Append("].[dbo].[");
            script.Append(targetEntity.MainTable.Name);
            script.AppendLine("] AS target");
            script.AppendLine("USING");
            script.AppendLine("(");
            script.AppendLine(SelectSourceEntityScript(source, sourceEntity));
            script.AppendLine(")");
            script.Append("AS source(");
            script.Append(AllFieldsScript(sourceEntity, string.Empty));
            script.AppendLine(")");
            script.AppendLine("ON");
            script.AppendLine(MergeSourceWithTargetConditionsScript(sourceEntity, targetEntity));
            script.AppendLine("WHEN MATCHED THEN");
            script.AppendLine(UpdateTargetEntityScript(target, targetEntity, "source", sourceEntity));
            script.AppendLine("WHEN NOT MATCHED THEN");
            script.Append(InsertTargetEntityScript(target, targetEntity, "source", sourceEntity));
            script.Append(";");
            return script.ToString();
        }
        private List<Property> GetPrimaryKeyProperties(Entity entity)
        {
            List<Property> list = new List<Property>();
            foreach (Property property in entity.Properties)
            {
                foreach (Field field in property.Fields)
                {
                    if (field.IsPrimaryKey)
                    {
                        list.Add(property);
                        break;
                    }
                }
            }
            return list;
        }
        private string MergeSourceWithTargetConditionsScript(Entity sourceEntity, Entity targetEntity)
        {
            string functionName = GetTypeCodesCorrespondenceFucntionName();
            List<Property> sourcePrimaryKey = GetPrimaryKeyProperties(sourceEntity);
            List<Property> targetPrimaryKey = GetPrimaryKeyProperties(targetEntity);

            StringBuilder script = new StringBuilder();
            foreach (Property targetProperty in targetPrimaryKey)
            {
                Property sourceProperty = sourcePrimaryKey.Where((p) => p.Name == targetProperty.Name).First();

                foreach (Field targetField in targetProperty.Fields)
                {
                    Field sourceField = sourceProperty.Fields.Where((f) => f.Purpose == targetField.Purpose).First();

                    if (script.Length > 0) { script.Append(" AND "); }

                    script.Append("target.[");
                    script.Append(targetField.Name);
                    script.Append("] = ");

                    if (targetField.Purpose == FieldPurpose.TypeCode)
                    {
                        script.Append(functionName);
                        script.Append(" (");
                    }

                    script.Append("source.[");
                    script.Append(sourceField.Name);
                    script.Append("]");

                    if (targetField.Purpose == FieldPurpose.TypeCode)
                    {
                        script.Append(")");
                    }
                }
            }
            return script.ToString();
        }
        private Entity GetEntityByName(InfoBase infoBase, string namespaceName, string entityName)
        {
            Entity result = null;
            IPersistentContext context = MetadataPersistentContext.Current;
            using (SqlConnection connection = new SqlConnection(context.ConnectionString))
            using (SqlCommand command = connection.CreateCommand())
            {
                command.CommandText = "[dbo].[get_entity_by_name]";
                command.CommandType = CommandType.StoredProcedure;
                command.Parameters.AddWithValue("infobase", infoBase.Identity);
                command.Parameters.AddWithValue("namespace_name", namespaceName);
                command.Parameters.AddWithValue("entity_name", entityName);
                connection.Open();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        result = context.Factory.New<Entity>(reader.GetGuid(0));
                    }
                }
            }
            return result;
        }
        
        private string GetReferenceFieldName(Entity entity)
        {
            if (entity.Owner != null)
            {
                return entity.Owner.MainTable.Name + "_IDRRef";
            }
            return "_IDRRef";
        }
        private int GetReferenceTypeCode(Entity entity)
        {
            if (entity.Owner != null)
            {
                return entity.Owner.Code;
            }
            return entity.Code;
        }

        // v2
        private List<Property> GetFilterProperties(MetadataTreeNode node)
        {
            List<Property> list = new List<Property>();

            BooleanExpression filter = node.Filter as BooleanExpression;
            if (filter == null || filter.Conditions.Count == 0)
            {
                return list;
            }

            foreach (IComparisonExpression condition in filter.Conditions)
            {
                PropertyExpression expression = condition.LeftExpression as PropertyExpression;
                list.Add((Property)expression.PropertyInfo);
            }

            return list;
        }
        private List<Property> GetForeignKeyProperties(Entity entity, List<Property> filter_properties)
        {
            List<Property> list = new List<Property>();

            foreach (Property property in entity.Properties)
            {
                //if (property.Purpose == PropertyPurpose.System) continue;
                if (property.Name == "Ссылка") continue;
                if (property.Relations.Count == 0) continue; // так может случиться, что я не поддерживаю некоторые ссылочные типы данных, например ПланВидовРасчетаСсылка

                bool isFilterProperty = false;
                foreach (Property filter in filter_properties)
                {
                    if (property == filter)
                    {
                        isFilterProperty = true;
                        break;
                    }
                }
                if (isFilterProperty) continue;

                foreach (Field field in property.Fields)
                {
                    if (field.Purpose == FieldPurpose.Object)
                    {
                        list.Add(property);
                        break;
                    }
                }
            }

            return list;
        }
        private string GetReferencesRegisterTableName()
        {
            return "[Z].[dbo].[Z_ReferencesRegisterTable]";
        }
        private void CreateReferencesRegisterTable()
        {
            string table_name = GetReferencesRegisterTableName();

            StringBuilder query = new StringBuilder();
            query.Append("IF (NOT OBJECT_ID(N'");
            query.Append(table_name);
            query.Append("') IS NULL) DROP TABLE ");
            query.Append(table_name);
            query.AppendLine(";");
            query.Append("CREATE TABLE ");
            query.Append(table_name);
            query.AppendLine(" (NODE uniqueidentifier, ENTITY int, OBJ_REF binary(16));");
            query.Append(@"CREATE INDEX IX_ReferencesRegisterTable_NODE ON ");
            query.Append(table_name);
            query.AppendLine(" (NODE);");
            query.Append("CREATE INDEX IX_ReferencesRegisterTable_ENTITY ON ");
            query.Append(table_name);
            query.Append(" (ENTITY, OBJ_REF);");
            
            using (SqlConnection connection = new SqlConnection(connection_string))
            using (SqlCommand command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandType = CommandType.Text;
                command.CommandText = query.ToString();
                int rowsAffected = command.ExecuteNonQuery();
            }
        }
        private void RegisterRootNodeReferences(MetadataTreeNode root)
        {
            DateTime period = (DateTime)this.Parameters["Period"];
            Guid department = (Guid)this.Parameters["Department"];

            Entity entity = (Entity)root.MetadataInfo;

            string table_name = string.Format("[{0}].[dbo].[{1}]", entity.InfoBase.Database, entity.MainTable.Name);
            DateTime start_of_period = new DateTime(period.Year, period.Month, period.Day, 0, 0, 0, 0);
            start_of_period = start_of_period.AddYears(2000); // fuck 1C !!!
            string branch = GetDepartmentFieldName(entity);

            StringBuilder query = new StringBuilder();
            query.Append("MERGE ");
            query.Append(GetReferencesRegisterTableName());
            query.AppendLine(" AS target");
            query.AppendLine("USING");
            query.AppendLine("(");
            query.Append("SELECT [_IDRRef] FROM ");
            query.Append(table_name);
            query.Append(" WHERE [_Date_Time] >= @period ");
            query.Append("AND [");
            query.Append(branch);
            query.AppendLine("] = @branch");
            query.AppendLine(")");
            query.AppendLine("AS source([_IDRRef])");
            query.AppendLine("ON (target.[NODE] = @node AND target.[ENTITY] = @entity AND target.[OBJ_REF] = source.[_IDRRef])");
            query.AppendLine("WHEN NOT MATCHED THEN");
            query.Append("INSERT ([NODE], [ENTITY], [OBJ_REF]) VALUES (@node, @entity, source.[_IDRRef]);");

            int rowsAffected = 0;
            using (SqlConnection connection = new SqlConnection(connection_string))
            using (SqlCommand command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandType = CommandType.Text;
                command.CommandText = query.ToString();
                command.Parameters.AddWithValue("node", root.Identity);
                command.Parameters.AddWithValue("entity", entity.Code);
                command.Parameters.AddWithValue("period", start_of_period);
                command.Parameters.AddWithValue("branch", department.ToByteArray());
                rowsAffected = command.ExecuteNonQuery();
            }
            root.Count = rowsAffected;

            WriteToLog("Priod = " + start_of_period.ToString("dd.MM.yyyy HH:mm:ss.ffff", CultureInfo.InvariantCulture));
            WriteToLog(query.ToString());
            WriteToLog("Count = " + root.Count.ToString() + Environment.NewLine);
        }

        // !!! =)
        public void RegisterNodeReferences(MetadataTreeNode node)
        {
            if (node.MetadataInfo is Namespace) throw new ArgumentException("Namespace can not register references", "node");

            if (node.Parent == null)
            {
                RegisterRootNodeReferences(node); // TODO: make references search by node's filter expression
                return;
            }

            Entity sourceEntity = (Entity)node.MetadataInfo;

            MetadataTreeNode parentNode = (sourceEntity.Owner == null)
                ? GetParentNode(node)
                : GetParentNode((MetadataTreeNode)node.Parent);

            // this is nested entities of the root entity which references has been already registered
            if (parentNode == null && sourceEntity.Owner != null) { return; }

            if (parentNode == null)
            {
                string message = "Parent node is not found"
                    + Environment.NewLine
                    + "Source: " + sourceEntity.Namespace.Name + "." + ((sourceEntity.Owner == null) ? "" : sourceEntity.Owner.Name + ".") + sourceEntity.Name;
                throw new ApplicationException(message);
            }
            
            Entity parentEntity = (Entity)parentNode.MetadataInfo;
            if (parentEntity == null) throw new ApplicationException("Node's entity can not be null");

            List<Property> filters = GetFilterProperties(node); // node's entity properties holding references to parent node's entity
            if (filters.Count == 0)
            {
                WriteToLog("--------------------------");
                WriteToLog("< RegisterNodeReferences >");
                WriteToLog("Source: " + sourceEntity.Namespace.Name + "." + ((sourceEntity.Owner == null) ? "" : sourceEntity.Owner.Name + ".") + sourceEntity.Name);
                WriteToLog("Parent: " + parentEntity.Namespace.Name + "." + parentEntity.Name);
                WriteToLog("None filter property is found");
                WriteToLog(Environment.NewLine);
                return; // look into nested entities
            }

            string referenceFieldName = GetReferenceFieldName(sourceEntity);
            string targetTable = GetReferencesRegisterTableName();
            
            StringBuilder query = new StringBuilder();
            query.Append("MERGE ");
            query.Append(targetTable);
            query.AppendLine(" AS target");
            query.AppendLine("USING");
            query.AppendLine("(");
            query.Append(SelectNodeReferencesScript(sourceEntity, filters, parentEntity));
            query.AppendLine(")");
            query.Append("AS source([");
            query.Append(referenceFieldName);
            query.AppendLine("])");
            query.Append("ON (target.[NODE] = @sourceNode AND target.[ENTITY] = @sourceEntity AND target.[OBJ_REF] = source.[");
            query.Append(referenceFieldName);
            query.AppendLine("])");
            query.AppendLine("WHEN NOT MATCHED THEN");
            query.Append("INSERT ([NODE], [ENTITY], [OBJ_REF]) VALUES (@sourceNode, @sourceEntity, source.[");
            query.Append(referenceFieldName);
            query.Append("]);");

            int rowsAffected = 0;
            using (SqlConnection connection = new SqlConnection(connection_string))
            using (SqlCommand command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandType = CommandType.Text;
                command.CommandText = query.ToString();
                command.Parameters.AddWithValue("parentNode", parentNode.Identity);
                command.Parameters.AddWithValue("parentEntity", parentEntity.Code);
                if (sourceEntity.Owner == null)
                {
                    command.Parameters.AddWithValue("sourceNode", node.Identity);
                }
                else
                {
                    command.Parameters.AddWithValue("sourceNode", ((MetadataTreeNode)node.Parent).Identity);
                }
                if (sourceEntity.Owner == null)
                {
                    command.Parameters.AddWithValue("sourceEntity", sourceEntity.Code);
                }
                else
                {
                    command.Parameters.AddWithValue("sourceEntity", sourceEntity.Owner.Code);
                }
                try
                {
                    rowsAffected = command.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    WriteToLog("--------------------------");
                    WriteToLog("< RegisterNodeReferences >");
                    WriteToLog("Source: " + sourceEntity.Namespace.Name + "." + ((sourceEntity.Owner == null) ? "" : sourceEntity.Owner.Name + ".") + sourceEntity.Name);
                    WriteToLog("Parent: " + parentEntity.Namespace.Name + "." + parentEntity.Name);
                    WriteToLog(query.ToString());
                    WriteToLog(GetErrorText(ex));
                    WriteToLog(ex.StackTrace);
                    WriteToLog(Environment.NewLine);
                    throw;
                }
            }
            node.Count = rowsAffected;
            if (sourceEntity.Owner != null)
            {
                node.Parent.Count += rowsAffected;
            }

            WriteToLog("--------------------------");
            WriteToLog("< RegisterNodeReferences >");
            WriteToLog("Source: " + sourceEntity.Namespace.Name + "." + ((sourceEntity.Owner == null) ? "" : sourceEntity.Owner.Name + ".") + sourceEntity.Name);
            WriteToLog("Parent: " + parentEntity.Namespace.Name + "." + parentEntity.Name);
            foreach (Property filter in filters)
            {
                WriteToLog(" - " + filter.Name);
            }
            WriteToLog(query.ToString());
            WriteToLog("Rows affected = " + rowsAffected.ToString() + Environment.NewLine);
        }
        private string SelectNodeReferencesScript(Entity source, List<Property> filters, Entity parent)
        {
            StringBuilder script = new StringBuilder();
            for (int i = 0; i < filters.Count; i++)
            {
                script.AppendLine(SelectNodeReferencesByOneFilterScript(source, filters[i], parent));
                if (filters.Count > 1 && i < filters.Count - 1)
                {
                    script.AppendLine("UNION");
                }
            }
            return script.ToString();
        }
        private string SelectNodeReferencesByOneFilterScript(Entity source, Property filter, Entity parent)
        {
            string sourceTable = string.Format("[{0}].[dbo].[{1}]", source.InfoBase.Database, source.MainTable.Name);

            StringBuilder script = new StringBuilder();
            script.Append("SELECT [");
            script.Append(GetReferenceFieldName(source));
            script.Append("] FROM ");
            script.Append(sourceTable);
            script.Append(" AS S INNER JOIN ");
            script.Append(GetReferencesRegisterTableName());
            script.AppendLine(" AS T ON");
            script.Append("T.[NODE] = @parentNode AND T.[ENTITY] = @parentEntity AND ");
            script.Append(NodeReferencesFilterScript(filter, parent, "S", "T"));
            return script.ToString();
        }
        private string NodeReferencesFilterScript(Property property, Entity parentEntity, string sourceTableAlias, string targetTableAlias)
        {
            StringBuilder script = new StringBuilder();

            if (property.Fields.Count == 1)
            {
                script.Append(string.Format("{0}.[{1}] = {2}.[OBJ_REF]", sourceTableAlias, property.Fields[0].Name, targetTableAlias));
            }
            else
            {
                string object_field = string.Empty;
                string locator_field = string.Empty;
                string type_code_field = string.Empty;
                foreach (IFieldInfo field in property.Fields)
                {
                    switch (field.Purpose)
                    {
                        case FieldPurpose.Object: { object_field = field.Name; break; }
                        case FieldPurpose.Locator: { locator_field = field.Name; break; }
                        case FieldPurpose.TypeCode: { type_code_field = field.Name; break; }
                    }
                }

                script.Append(string.Format(
                        "{0}.[{1}] = CAST({2} AS binary(4)) AND {0}.[{3}] = {4}.[OBJ_REF]",
                        sourceTableAlias, type_code_field, parentEntity.Code, object_field, targetTableAlias));

                if (locator_field != string.Empty)
                {
                    script.Append(string.Format(" AND {0}.[{1}] = 0x08", sourceTableAlias, locator_field));
                }
            }

            return script.ToString();
        }

        /// <summary>
        /// Процедура отбирает объекты узла данных,
        /// по настроенному для него фильтру,
        /// а затем регистрирует внешние ссылки его свойств для обмена.
        /// Важно: свойства, используемые для фильтрации,
        /// не используются для поиска внешних объектов!
        /// </summary>
        /// <param name="node">
        /// Узел данных, для объектов которого
        /// необходимо выполнить регистрацию
        /// внешних ссылок для обмена.
        /// </param>
        /// <returns></returns>
        public void RegisterNodeForeignReferences(MetadataTreeNode node)
        {
            Entity sourceEntity = node.MetadataInfo as Entity;
            if (sourceEntity == null) { throw new ArgumentOutOfRangeException("node"); }

            MetadataTreeNode parentNode = (sourceEntity.Owner == null)
                ? GetParentNode(node)
                : GetParentNode((MetadataTreeNode)node.Parent);
            Entity parentEntity = (parentNode == null) ? null : (Entity)parentNode.MetadataInfo;

            List<Property> filters = GetFilterProperties(node);
            List<Property> foreigners = GetForeignKeyProperties(sourceEntity, filters);

            if (sourceEntity.Owner != null
                && filters.Where((f) => f.Name == "Ссылка").FirstOrDefault() != null)
            //&& sourceEntity.Owner == filters.Where((f) => f.Name == "Ссылка").FirstOrDefault().Entity)
            {
                parentNode = (MetadataTreeNode)node.Parent;
                parentEntity = (Entity)parentNode.MetadataInfo;
            }

            if (filters.Count == 0 && parentNode != null)
            {
                WriteToLog("--------------------------");
                WriteToLog("< RegisterNodeForeignReferences >");
                WriteToLog("Source: " + sourceEntity.Namespace.Name + "." + ((sourceEntity.Owner == null) ? "" : sourceEntity.Owner.Name + ".") + sourceEntity.Name);
                WriteToLog("Parent: " + ((parentEntity == null) ? "none" : parentEntity.Namespace.Name + "." + parentEntity.Name));
                WriteToLog("None filter property is found");
                WriteToLog(Environment.NewLine);
                return;
            }
            if (foreigners.Count == 0)
            {
                WriteToLog("--------------------------");
                WriteToLog("< RegisterNodeForeignReferences >");
                WriteToLog("Source: " + sourceEntity.Namespace.Name + "." + ((sourceEntity.Owner == null) ? "" : sourceEntity.Owner.Name + ".") + sourceEntity.Name);
                WriteToLog("Parent: " + ((parentEntity == null) ? "none" : parentEntity.Namespace.Name + "." + parentEntity.Name));
                WriteToLog("None foreign reference property is found");
                WriteToLog(Environment.NewLine);
                return;
            }

            string query = BuildSelectForeignKeysScript(parentEntity, sourceEntity, filters, foreigners);

            DateTime period = (DateTime)this.Parameters["Period"];
            period = new DateTime(period.Year, period.Month, period.Day, 0, 0, 0, 0);
            period = period.AddYears(2000); // fuck 1C !!!
            Guid department = (Guid)this.Parameters["Department"];

            int rowsAffected = 0;
            using (SqlConnection connection = new SqlConnection(connection_string))
            using (SqlCommand command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandType = CommandType.Text;
                command.CommandText = query.ToString();
                command.Parameters.AddWithValue("sourceNode", Guid.Empty);
                if (parentNode == null)
                {
                    command.Parameters.AddWithValue("period", period);
                    command.Parameters.AddWithValue("branch", department.ToByteArray());
                }
                else
                {
                    command.Parameters.AddWithValue("parentNode", parentNode.Identity);
                    command.Parameters.AddWithValue("parentEntity", parentEntity.Code);
                }
                
                try
                {
                    rowsAffected = command.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    WriteToLog("--------------------------");
                    WriteToLog("< RegisterNodeForeignReferences >");
                    WriteToLog("Source: " + sourceEntity.Namespace.Name + "." + ((sourceEntity.Owner == null) ? "" : sourceEntity.Owner.Name + ".") + sourceEntity.Name);
                    WriteToLog("Parent: " + ((parentEntity == null) ? "none" : parentEntity.Namespace.Name + "." + parentEntity.Name));
                    WriteToLog(query.ToString());
                    WriteToLog(GetErrorText(ex));
                    WriteToLog(ex.StackTrace);
                    WriteToLog(Environment.NewLine);
                    throw;
                }

                WriteToLog("---------------------------------");
                WriteToLog("< RegisterNodeForeignReferences >");
                WriteToLog("Source: " + sourceEntity.Namespace.Name + "." + ((sourceEntity.Owner == null) ? "" : sourceEntity.Owner.Name + ".") + sourceEntity.Name);
                WriteToLog("Parent: " + ((parentEntity == null) ? "none" : parentEntity.Namespace.Name + "." + parentEntity.Name));
                foreach (Property filter in filters)
                {
                    WriteToLog(" pk " + filter.Name);
                }
                foreach (Property foreiner in foreigners)
                {
                    WriteToLog(" fk " + foreiner.Name);
                }
                WriteToLog(query.ToString());
                WriteToLog("Rows affected = " + rowsAffected.ToString() + Environment.NewLine);
            }
        }

        private string GetTypeCodesCorrespondenceFucntionName()
        {
            return "[dbo].[Z_GetTargetTypeCode]";
        }
        private void CreateTypeCodesCorrespondenceFucntion(List<Entity> sourceEntities, InfoBase targetInfoBase)
        {
            MetadataService service = new MetadataService();
            StringBuilder script = new StringBuilder();
            List<string> commands = new List<string>();

            string functionName = GetTypeCodesCorrespondenceFucntionName();

            script.AppendLine("USE [Z];");
            //script.AppendLine("GO");
            commands.Add(script.ToString());

            script.Clear();
            script.AppendLine("IF OBJECT_ID(N'" + functionName + "', N'FN') IS NOT NULL DROP FUNCTION " + functionName + ";");
            //script.AppendLine("GO");
            commands.Add(script.ToString());

            script.Clear();
            script.AppendLine("CREATE FUNCTION " + functionName + " (@sourceTypeCode binary(4))");
            script.AppendLine("RETURNS binary(4)");
            script.AppendLine("AS");
            script.AppendLine("BEGIN");
            script.AppendLine("DECLARE @targetTypeCode binary(4);");
            script.AppendLine("SELECT @targetTypeCode = (CASE");
            foreach (Entity sourceEntity in sourceEntities)
            {
                int sourceTypeCode = sourceEntity.Code;
                int targetTypeCode = service.GetEntityInfo(targetInfoBase, sourceEntity.Namespace.Name, sourceEntity.Name).Code;
                script.Append("WHEN CAST(@sourceTypeCode AS int) = ");
                script.Append(sourceTypeCode.ToString());
                script.Append(" THEN CAST(");
                script.Append(targetTypeCode.ToString());
                script.AppendLine(" AS binary(4))");
            }
            script.AppendLine("ELSE 0x00000000");
            script.AppendLine("END);");
            script.AppendLine("RETURN @targetTypeCode;");
            script.AppendLine("END;");
            //script.AppendLine("GO");
            commands.Add(script.ToString());

            IPersistentContext context = MetadataPersistentContext.Current;
            using (SqlConnection connection = new SqlConnection(context.ConnectionString))
            using (SqlCommand command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandType = CommandType.Text;
                foreach (string sql in commands)
                {
                    command.CommandText = sql;
                    int rowsAffected = command.ExecuteNonQuery();
                }
            }
        }
    }
}

// 0x00000000000000000000000000000000
// '00000000-0000-0000-0000-000000000000'