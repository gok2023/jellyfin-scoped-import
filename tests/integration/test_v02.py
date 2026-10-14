"""Real local lab imports: anime folder layout and configured provider order."""
import copy,json,time,uuid
from pathlib import Path
from support import lab
from support.lab import connect

def run():
    connect()
    assert lab.require('GET','/ScopedImport/Libraries')['Version']=='0.2.0'
    report={'cases':[],'status':'running','production_accessed':False}
    output=lab.ROOT/'evidence/scoped-v02.json'
    def save():output.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    baseline={x['Id']:x for x in lab.snapshot()['Items']}
    start=lab.require('GET','/ScopedImport/Audit')['Sequence']
    def execute(name,path,count,fetch=False):
        hashes=lab.file_hashes()
        operation=lab.require('POST','/ScopedImport/Import',{'Path':str(path),'FetchMetadata':fetch},202)['OperationId']
        for _ in range(200):
            result=lab.require('GET','/ScopedImport/Operations/'+operation)
            if result['State'] in ('Completed','Rejected','Partial'):break
            time.sleep(.3)
        report['cases'].append({'name':name,'result':result});save()
        assert result['State']=='Completed',result
        assert len(result['Created'])==count,result
        assert hashes==lab.file_hashes(),'Media changed'
        for directory in result['EnumeratedDirectories']:
            assert Path(directory).is_relative_to(path if path.is_dir() else path.parent)
        return result
    label=uuid.uuid4().hex[:8]
    series=lab.LAB/'media/tv'/('Layout Fixture '+label)
    for number in range(1,13):lab.video(f'tv/{series.name}/E{number:02d}「Fixture Subtitle」 [BDRip][avc_aacx2_chap][1080p]/(7E562D56).mp4')
    try:
        anime=execute('12_crc_files_in_episode_folders',series,14)
        assert [x['Type'] for x in anime['Created']].count('Season')==1
        current={x['Id']:x for x in lab.snapshot()['Items']}
        episodes=[current[x['Id']] for x in anime['Created'] if x['Type']=='Episode']
        assert sorted(x['IndexNumber'] for x in episodes)==list(range(1,13))
        assert all(x['ParentIndexNumber']==1 for x in episodes)
        execute('repeat_anime',series,0)
        ep=lab.video(f'tv/{series.name}/E13「Next Fixture」 [BDRip]/(ABCDEF12).mp4')
        execute('add_episode_to_existing_pathless_season',ep,1)
        folder=next(x for x in lab.require('GET','/Library/VirtualFolders') if x['Name']=='Lab Other')
        original=folder['LibraryOptions']
        try:
            for name,enabled,order,expected in [
                ('library_A_first',['Lab-A','Lab-B'],['Lab-A','Lab-B'],'Lab-A'),
                ('library_B_first',['Lab-A','Lab-B'],['Lab-B','Lab-A'],'Lab-B'),
                ('disabled_provider_not_called',['Lab-B'],['Lab-A','Lab-B'],'Lab-B'),
                ('provider_failure_and_write_block',['Lab-Failure','Lab-BlockedWriter','Lab-B'],['Lab-Failure','Lab-BlockedWriter','Lab-B'],'Lab-B')]:
                options=copy.deepcopy(original)
                for t in options['TypeOptions']:
                    if t['Type']=='Movie':t['MetadataFetchers']=enabled;t['MetadataFetcherOrder']=order
                lab.require('POST','/Library/VirtualFolders/LibraryOptions',{'Id':folder['ItemId'],'LibraryOptions':options},204)
                video=lab.video(f'other/Provider {name} {label}/test.mp4')
                result=execute(name,video.parent,1,True)
                called=[x['Provider'] for x in result['MetadataProviders']]
                assert called==[p for p in order if p in enabled],called
                item=next(x for x in lab.snapshot()['Items'] if x['Id']==result['Created'][0]['Id'])
                assert item['Name']==expected,item
                assert item['Overview']=='fallback overview',item
                if name=='library_A_first':assert item['ProviderIds']['Lab-Forwarded']=='yes'
                if name=='provider_failure_and_write_block':assert [x['Outcome'] for x in result['MetadataProviders']]==['Error','Error','Metadata']
            video=lab.video(f'other/Provider disabled choice {label}/test.mp4')
            result=execute('metadata_option_off',video.parent,1,False)
            assert result['MetadataProviders']==[]
        finally:lab.require('POST','/Library/VirtualFolders/LibraryOptions',{'Id':folder['ItemId'],'LibraryOptions':original},204)
        final={x['Id']:x for x in lab.snapshot()['Items']}
        def stable(item):
            item=copy.deepcopy(item)
            if item.get('IsFolder'):item.get('UserData',{}).pop('UnplayedItemCount',None)
            return item
        assert all(k in final and stable(v)==stable(final[k]) for k,v in baseline.items()),'Old fields changed'
        audit=lab.require('GET','/ScopedImport/Audit')
        writes=[x for x in audit['Entries'] if x['Sequence']>start]
        assert all(x['Method']=='SaveItems' and x['AppendOnly'] for x in writes),writes
        assert not(set(baseline)&{i.replace('-','') for w in writes for i in w['Ids']})
        report.update(status='passed',existing_items_preserved=True,repository_writes=writes)
        save();print('PASS: 8 v0.2 integration cases, anime episode folders, provider order/fallback/disable/write guard, existing-item audit')
    except Exception as ex:
        report.update(status='failed',error=str(ex));save();raise

if __name__=='__main__':run()
