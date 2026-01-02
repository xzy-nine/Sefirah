using Sefirah.Data.AppDatabase.Models;
using Sefirah.Data.Models;

namespace Sefirah.Data.AppDatabase.Repository;

public class SmsRepository(DatabaseContext databaseContext, ILogger logger)
{

    #region Conversation Operations

    public async Task<ConversationEntity?> GetConversationAsync(string deviceId, long threadId)
    {
        try
        {
            return await Task.Run(() => 
                databaseContext.Database.Table<ConversationEntity>()
                    .FirstOrDefault(c => c.DeviceId == deviceId && c.ThreadId == threadId));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取设备 {DeviceId} 会话 {ThreadId} 时出错", deviceId, threadId);
            return null;
        }
    }

    public async Task<List<ConversationEntity>> GetConversationsAsync(string deviceId)
    {
        try
        {
            return await Task.Run(() => 
                databaseContext.Database.Table<ConversationEntity>()
                    .Where(c => c.DeviceId == deviceId)
                    .OrderByDescending(c => c.LastMessageTimestamp)
                    .ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取设备 {DeviceId} 的会话列表时出错", deviceId);
            return [];
        }
    }

    public async Task<bool> SaveConversationAsync(ConversationEntity conversation)
    {
        try
        {
            await Task.Run(() => databaseContext.Database.InsertOrReplace(conversation));
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存设备 {DeviceId} 的会话 {ThreadId} 时出错", conversation.DeviceId, conversation.ThreadId);
            return false;
        }
    }

    public async Task<bool> DeleteConversationAsync(string deviceId, long threadId)
    {
        try
        {
            await Task.Run(() =>
            {
                // Delete conversation
                databaseContext.Database.Delete<ConversationEntity>(threadId);
                
                // Delete associated messages
                databaseContext.Database.Execute("DELETE FROM TextMessageEntity WHERE DeviceId = ? AND ThreadId = ?", deviceId, threadId);
                
            });
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "删除设备 {DeviceId} 的会话 {ThreadId} 时出错", deviceId, threadId);
            return false;
        }
    }

    #endregion

    #region Message Operations

    public async Task<List<MessageEntity>> GetMessagesAsync(string deviceId, long threadId)
    {
        try
        {
            return await Task.Run(() => 
                databaseContext.Database.Table<MessageEntity>()
                    .Where(m => m.DeviceId == deviceId && m.ThreadId == threadId)
                    .OrderBy(m => m.Timestamp)
                    .ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取设备 {DeviceId} 会话 {ThreadId} 的消息时出错", deviceId, threadId);
            return [];
        }
    }

    public async Task<List<MessageEntity>> GetMessagesWithAttachmentsAsync(string deviceId, long threadId)
    {
        try
        {
            var messages = await GetMessagesAsync(deviceId, threadId);
            
            //// Load attachments for each message
            //foreach (var message in messages)
            //{
            //    var attachments = await GetAttachmentsAsync(message.UniqueId);
            //    // Convert attachments to TextMessage.Attachments format if needed
            //    if (attachments.Count > 0)
            //    {
            //        //message.Attachments = attachments.Select(a => new SmsAttachment
            //        //{
            //        //    Base64Data = a.Data
            //        //}).ToList();
            //    }
            //}
            
            return messages;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取带附件的消息（设备：{DeviceId}, 会话：{ThreadId}）时出错", deviceId, threadId);
            return [];
        }
    }

    public List<MessageEntity>? GetMessageAsync(string deviceId, long uniqueId)
    {
        try
        {
            return databaseContext.Database.Table<MessageEntity>().Where(m => m.DeviceId == deviceId && m.UniqueId == uniqueId).ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取设备 {DeviceId} 的消息 {UniqueId} 时出错", deviceId, uniqueId);
            return null;
        }
    }

    public async Task<bool> SaveMessageAsync(MessageEntity message)
    {
        try
        {
            await Task.Run(() => databaseContext.Database.InsertOrReplace(message));
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存设备 {DeviceId} 的消息 {UniqueId} 时出错", message.DeviceId, message.UniqueId);
            return false;
        }
    }

    public async Task<bool> SaveMessagesAsync(List<MessageEntity> messages)
    {
        try
        {
            await Task.Run(() => databaseContext.Database.InsertAll(messages, "OR REPLACE"));
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存批量消息时出错");
            return false;
        }
    }

    public async Task<bool> DeleteMessageAsync(string deviceId, long uniqueId)
    {
        try
        {
            await Task.Run(() =>
            {
                databaseContext.Database.Execute("DELETE FROM TextMessageEntity WHERE DeviceId = ? AND UniqueId = ?", deviceId, uniqueId);
                // Note: Attachment deletion removed for now as per user request
            });
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "删除设备 {DeviceId} 的消息 {UniqueId} 时出错", deviceId, uniqueId);
            return false;
        }
    }

    #endregion

    #region Contact Operations

    public async Task<List<ContactEntity>> GetContactsAsync(string deviceId)
    {
        try
        {
            return await Task.Run(() => 
                databaseContext.Database.Table<ContactEntity>()
                    .Where(c => c.DeviceId == deviceId)
                    .ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取设备 {DeviceId} 联系人时出错", deviceId);
            return [];
        }
    }

    public async Task<List<ContactEntity>> GetAllContactsAsync()
    {
        try
        {
            return await Task.Run(() =>
            {
                return databaseContext.Database.Table<ContactEntity>().ToList();
            });
        }
        catch
        {
            return [];
        }
    }


    public async Task<ContactEntity?> GetContactAsync(string deviceId, string phoneNumber)  
    {
        try
        {
            return await Task.Run(() => 
                databaseContext.Database.Table<ContactEntity>()
                    .FirstOrDefault(c => c.DeviceId == deviceId && c.Number == phoneNumber));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取设备 {DeviceId} 的联系人 {PhoneNumber} 时出错", deviceId, phoneNumber);
            return null;
        }
    }

    public async Task<ContactEntity?> GetContactByIdAsync(string deviceId, string contactId)
    {
        try
        {
            return await Task.Run(() => 
                databaseContext.Database.Table<ContactEntity>()
                    .FirstOrDefault(c => c.DeviceId == deviceId && c.Id == contactId));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "根据 ID 获取设备 {DeviceId} 的联系人 {ContactId} 时出错", deviceId, contactId);
            return null;
        }
    }

    public async Task<bool> SaveContactAsync(ContactEntity contact)
    {
        try
        {
            await Task.Run(() => databaseContext.Database.InsertOrReplace(contact));
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存设备 {DeviceId} 的联系人 {PhoneNumber} 时出错", contact.DeviceId, contact.Number);
            return false;
        }
    }

    public async Task<bool> SaveContactsAsync(List<ContactEntity> contacts)
    {
        try
        {
            await Task.Run(() => databaseContext.Database.InsertAll(contacts, "OR REPLACE"));
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存批量联系人时出错");
            return false;
        }
    }

    #endregion

    #region Attachment Operations

    public async Task<List<AttachmentEntity>> GetAttachmentsAsync(long messageUniqueId)
    {
        try
        {
            return await Task.Run(() => 
                databaseContext.Database.Table<AttachmentEntity>()
                    .Where(a => a.MessageUniqueId == messageUniqueId)
                    .ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取消息附件（MessageUniqueId：{MessageUniqueId}）时出错", messageUniqueId);
            return [];
        }
    }

    public async Task<bool> SaveAttachmentAsync(AttachmentEntity attachment)
    {
        try
        {
            await Task.Run(() => databaseContext.Database.InsertOrReplace(attachment));
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存消息附件（MessageUniqueId：{MessageUniqueId}）时出错", attachment.MessageUniqueId);
            return false;
        }
    }

    public async Task<bool> SaveAttachmentsAsync(List<AttachmentEntity> attachments)
    {
        try
        {
            await Task.Run(() => databaseContext.Database.InsertAll(attachments, "OR REPLACE"));
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存批量附件时出错");
            return false;
        }
    }

    #endregion

    #region Helper Methods

    public static MessageEntity ToEntity(TextMessage message, string deviceId)
    {
        return new MessageEntity
        {
            UniqueId = message.UniqueId,
            ThreadId = message.ThreadId ?? 0,
            DeviceId = deviceId,
            Body = message.Body,
            Timestamp = message.Timestamp,
            Read = message.Read,
            SubscriptionId = message.SubscriptionId,
            MessageType = message.MessageType,
            Address = message.Addresses[0] // 0 index is for sender
        };
    }

    public static ContactEntity ToEntity(ContactMessage contact, string deviceId)
    {
        byte[]? avatar = null;
        if (!string.IsNullOrEmpty(contact.PhotoBase64))
        {
            try
            {
                avatar = Convert.FromBase64String(contact.PhotoBase64);
            }
            catch (Exception)
            {
                avatar = null;
            }
        }

        return new ContactEntity
        {
            Id = contact.Id,
            DeviceId = deviceId,
            LookupKey = contact.LookupKey,
            DisplayName = contact.DisplayName,
            Number = contact.Number,
            Avatar = avatar
        };
    }

    public static AttachmentEntity ToEntity(SmsAttachment attachment, long messageUniqueId)
    {
        return new AttachmentEntity
        {
            MessageUniqueId = messageUniqueId,
            Data = Convert.FromBase64String(attachment.Base64Data)
        };
    }

    public static ConversationEntity ToEntity(TextConversation textConversation, string deviceId)
    {
        var latestMessage = textConversation.Messages.OrderByDescending(m => m.Timestamp).First();

        Debug.WriteLine($"latestMessage: {latestMessage.Body} AddressesJson: {JsonSerializer.Serialize(textConversation.Recipients)}");
        return new ConversationEntity
        {
            ThreadId = textConversation.ThreadId,
            DeviceId = deviceId,
            AddressesJson = JsonSerializer.Serialize(textConversation.Recipients),
            HasRead = textConversation.Messages.Any(m => m.Read),
            TimeStamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            LastMessageTimestamp = latestMessage.Timestamp,
            LastMessage = latestMessage.Body
        };
    }
    #endregion
} 
