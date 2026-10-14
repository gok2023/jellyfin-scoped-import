"""Actual imports on the fixed, disposable 18096 instance. No scan endpoint."""
import datetime
import json
import time
import uuid
from pathlib import Path
from support import lab
from support.lab import connect


def run():
    user = connect()
    assert lab.require('GET', '/ScopedImport/Libraries')['Ready']
    report = {'kind': 'actual-isolated-import', 'start': datetime.datetime.now().astimezone().isoformat(), 'cases': [],
              'os_wide_io_trace': False, 'validate_children_instrumented': False}
    evidence = lab.ROOT / 'evidence/scoped-import.json'
    def save(): evidence.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    previous = {x['Id']:x for x in lab.snapshot()['Items']}
    audit_start = lab.require('GET','/ScopedImport/Audit')['Sequence']
    anonymous,_=lab.api('POST','/ScopedImport/Import',{'Path':str(lab.LAB/'media/tv')},token=None)
    assert anonymous==401,anonymous
    # Verify authorization with a fresh normal-user fixture.
    password=uuid.uuid4().hex
    ordinary=lab.require('POST','/Users/New',{'Name':'ScopedOrdinary'+password[:8],'Password':password})
    login=lab.require('POST','/Users/AuthenticateByName',{'Username':ordinary['Name'],'Pw':password})
    forbidden,_=lab.api('POST','/ScopedImport/Import',{'Path':str(lab.LAB/'media/tv')},token=login['AccessToken'])
    assert forbidden==403,forbidden
    report['anonymous_rejected']=anonymous;report['ordinary_user_rejected']=forbidden
    report['before']=previous
    label = uuid.uuid4().hex[:8]
    movie = lab.video(f'other/Scoped Movie {label}/Scoped.Movie.mp4')
    movie.with_suffix('.nfo').write_text('<movie><title>定向电影 '+label+'</title><plot>本地 NFO 识别验证</plot><uniqueid type="tmdb">603</uniqueid></movie>',encoding='utf-8')
    show = lab.LAB/'media/tv'/('Scoped Show '+label)
    for season in (1,2):
        lab.video(f'tv/{show.name}/Season {season:02d}/Scoped.Show.S{season:02d}E01.mp4')
    (show/'tvshow.nfo').write_text('<tvshow><title>定向电视剧 '+label+'</title><plot>剧目 NFO</plot></tvshow>',encoding='utf-8')
    def execute(name, path, expected_count=None, fetch=False):
        hashes = lab.file_hashes()
        state = lab.require('POST','/ScopedImport/Import',{'Path':str(path),'FetchMetadata':fetch},202)
        for _ in range(150):
            status=lab.require('GET','/ScopedImport/Operations/'+state['OperationId'])
            if status['State'] in ('Completed','Rejected','Partial'):break
            time.sleep(0.4)
        else:raise RuntimeError('Import wait timeout')
        report['cases'].append({'name':name,'path':str(path),'result':status});save()
        if expected_count is not None:
            assert status['State']=='Completed',status
            assert len(status['Created'])==expected_count,status
        assert hashes==lab.file_hashes(),'Media bytes changed'
        for directory in status['EnumeratedDirectories']:
            assert Path(directory).is_relative_to(path if path.is_dir() else path.parent)
        return status
    try:
        m=execute('new_movie',movie.parent,1)
        mid=m['Created'][0]['Id']
        current={x['Id']:x for x in lab.snapshot()['Items']}
        assert current[mid]['Name']=='定向电影 '+label
        assert current[mid]['Overview']=='本地 NFO 识别验证'
        assert current[mid]['ProviderIds']['Tmdb']=='603'
        lab.require('POST',f'/UserPlayedItems/{mid}?userId={user}')
        watched=lab.require('GET',f'/UserItems/{mid}/UserData?userId={user}')
        execute('new_multiseason_show',show,5)
        lab.video(f'tv/{show.name}/Season 03/Scoped.Show.S03E01.mp4')
        execute('new_season',show/'Season 03',2)
        ep=lab.video(f'tv/{show.name}/Season 01/Scoped.Show.S01E02.mp4')
        execute('new_episode',ep,1)
        stable={x['Id']:x for x in lab.snapshot()['Items']}
        execute('repeat_movie',movie.parent,0)
        execute('repeat_show',show,0)
        assert stable=={x['Id']:x for x in lab.snapshot()['Items']}
        assert watched==lab.require('GET',f'/UserItems/{mid}/UserData?userId={user}')
        missing=execute('unavailable_path',show/'Offline Season')
        assert missing['State']=='Rejected' and not missing['Created']
        rejected=execute('library_root_rejected',lab.LAB/'media/tv')
        assert rejected['State']=='Rejected' and not rejected['Created']
        for name,path in [('outside_library',lab.ROOT/'evidence'),('normalized_escape',str(lab.LAB/'media/tv')+'/../../logs')]:
            rejection=execute(name,path)
            assert rejection['State']=='Rejected' and not rejection['Created']
        final={x['Id']:x for x in lab.snapshot()['Items']}
        report['after']=final
        report['differences']={k:{field:{'before':v.get(field),'after':final.get(k,{}).get(field)} for field in set(v)|set(final.get(k,{})) if v.get(field)!=final.get(k,{}).get(field)} for k,v in previous.items() if final.get(k)!=v}
        save()
        def persistent_fields(item):
            value=json.loads(json.dumps(item))
            # Folder aggregate, computed from children; retain every other field.
            if value.get('IsFolder'):
                value.get('UserData',{}).pop('UnplayedItemCount',None)
            return value
        assert all(k in final and persistent_fields(final[k])==persistent_fields(v) for k,v in previous.items()),'Pre-existing fields changed beyond computed folder counts'
        audit=lab.require('GET','/ScopedImport/Audit')
        writes=[x for x in audit['Entries'] if x['Sequence']>audit_start]
        report['repository_writes']=writes
        assert len(writes)==audit['Sequence']-audit_start,'Audit truncated'
        assert all(x['Method']=='SaveItems' and x['AppendOnly'] for x in writes),writes
        assert not ({i.replace('-','') for x in writes for i in x['Ids']} & set(previous)),'Existing item saved'
        report['preexisting_fields_unchanged_except_computed_folder_counts']=True
        report['preexisting_repository_items_not_saved_or_deleted']=True
        report['repeat_import_preserved_watch_state']=True
        report['status']='passed';save()
        print('PASS: movie, 2-season show, new season, new episode, repeat/played-state, unavailable path and root rejection')
    except Exception as ex:
        report['status']='incomplete';report['error']=str(ex);save();raise


def verify_after_restart():
    connect()
    path=lab.ROOT/'evidence/scoped-import.json'
    report=json.loads(path.read_text(encoding='utf-8'))
    current={x['Id']:x for x in lab.snapshot()['Items']}
    for case in report['cases']:
        for item in case['result']['Created']:
            expected=report['after'][item['Id']];actual=current[item['Id']]
            for key in ('Name','Type','Path','ParentId','IndexNumber','ParentIndexNumber','ProviderIds','Overview'):
                assert expected.get(key)==actual.get(key),(item['Id'],key)
            if item['Type'] in ('Movie','Episode'):
                result=lab.require('GET','/Items?Ids='+item['Id']+'&Fields=MediaStreams')['Items'][0]
                assert any(x['Type']=='Video' for x in result.get('MediaStreams',[])),result
    report['created_items_and_video_streams_verified_after_restart']=True
    path.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print('PASS: new item metadata, hierarchy and video stream records persisted after restart')


if __name__=='__main__':run()
