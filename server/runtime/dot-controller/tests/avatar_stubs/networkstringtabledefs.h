// Minimal server table surface for testing the production avatar publisher.
#pragma once

struct SetStringUserDataRequest_t
{
    void *m_pRawData;
    unsigned int m_cbDataSize;
};

class INetworkStringTable
{
public:
    virtual ~INetworkStringTable() = default;
    virtual int FindStringIndex(const char *key) = 0;
    virtual const SetStringUserDataRequest_t *GetStringUserData(int index) = 0;
    virtual int GetNumStrings() = 0;
    virtual int AddString(bool server, const char *key, const SetStringUserDataRequest_t *data) = 0;
    virtual bool SetStringUserData(int index, const SetStringUserDataRequest_t *data, bool force) = 0;
};

class INetworkStringTableContainer
{
public:
    virtual ~INetworkStringTableContainer() = default;
    virtual INetworkStringTable *FindTable(const char *name) = 0;
};
